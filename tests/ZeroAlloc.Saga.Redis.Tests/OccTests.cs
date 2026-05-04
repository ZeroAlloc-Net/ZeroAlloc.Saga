using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga.Redis.Tests.Fixtures;

namespace ZeroAlloc.Saga.Redis.Tests;

/// <summary>
/// Optimistic-concurrency tests for <see cref="RedisSagaStore{TSaga,TKey}"/> and
/// the generator-emitted scope-per-attempt retry loop. Concurrency is enforced
/// via <c>WATCH</c> + <c>MULTI/EXEC</c>; conflicts surface as
/// <see cref="RedisSagaConcurrencyException"/>, which the handler's
/// <c>IsBackendConflict</c> filter catches alongside EfCore exceptions.
/// </summary>
public sealed class OccTests : IAsyncLifetime
{
    private readonly RedisFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    Task IAsyncLifetime.DisposeAsync() => _fx.DisposeAsync().AsTask();

    [Fact]
    public async Task RedisSagaStore_VersionMismatch_ThrowsRedisSagaConcurrencyException()
    {
        // Two store instances loading the same key, both saving — second save
        // observes version mismatch and throws.
        var db = _fx.Multiplexer.GetDatabase();
        var options = new RedisSagaStoreOptions { KeyPrefix = $"saga-occ-{Guid.NewGuid():N}" };

        var storeA = new RedisSagaStore<OrderFulfillmentSaga, OrderId>(db, options, NullLogger<RedisSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);
        var storeB = new RedisSagaStore<OrderFulfillmentSaga, OrderId>(db, options, NullLogger<RedisSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);

        var orderId = new OrderId(101);

        // Seed the saga so both stores observe a non-null version on load.
        var seed = await storeA.LoadOrCreateAsync(orderId, default);
        seed.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        seed.ReserveStock(new OrderPlaced(orderId, 10m));
        await storeA.SaveAsync(orderId, seed, default);

        // Both stores load the saved saga.
        var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
        var sagaB = await storeB.LoadOrCreateAsync(orderId, default);

        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sagaA.ChargeCustomer(new StockReserved(orderId));

        sagaB.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sagaB.ChargeCustomer(new StockReserved(orderId));

        // First save wins; rotates the version field.
        await storeA.SaveAsync(orderId, sagaA, default);

        // Second save observes the stale version and throws.
        await Assert.ThrowsAsync<RedisSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaB, default).ConfigureAwait(false));
    }

    [Fact]
    public async Task HandlerRetryLoop_RecoversFromTransientConflict()
    {
        // Wrap the saga store with a one-shot conflict thrower; the
        // scope-per-attempt retry loop in the generator-emitted handler
        // resolves a fresh wrapper on retry, sees no conflict the second
        // time, and the saga progresses normally.
        SagaStoreRegistrar.Reset();
        var counter = new SharedAttemptCounter();
        var keyPrefix = $"saga-occ-retry-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddSingleton(_fx.Multiplexer);
        services.AddSaga()
            .WithRedisStore(opts =>
            {
                opts.MaxRetryAttempts = 3;
                opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                opts.UseExponentialBackoff = false;
                opts.KeyPrefix = keyPrefix;
            })
            .WithOrderFulfillmentSaga();

        // Decorate the Redis store with a one-shot conflict wrapper. The shared
        // counter spans the per-attempt scopes.
        for (int i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ISagaStore<OrderFulfillmentSaga, OrderId>))
                services.RemoveAt(i);
        }
        services.AddScoped<ISagaStore<OrderFulfillmentSaga, OrderId>>(sp =>
        {
            var inner = new RedisSagaStore<OrderFulfillmentSaga, OrderId>(
                sp.GetRequiredService<IDatabase>(),
                sp.GetRequiredService<RedisSagaStoreOptions>(),
                NullLogger<RedisSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);
            return new OneShotConflictStore(inner, counter, keyPrefix);
        });

        var sp = services.BuildServiceProvider();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(202);
        // Should NOT throw — the retry loop catches the simulated conflict.
        await PublishAsync(sp, new OrderPlaced(orderId, 1m));

        // ReserveStock dispatched at least once on the successful retry.
        Assert.True(ledger.CommandsOfType<ReserveStockCommand>().Count >= 1,
            "Expected ReserveStock dispatched at least once after retry recovery");
    }

    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        using var scope = sp.CreateScope();
        foreach (var h in scope.ServiceProvider.GetServices<INotificationHandler<T>>())
            await h.Handle(evt, default).ConfigureAwait(false);
    }

    private sealed class SharedAttemptCounter
    {
        private int _saves;
        public bool TryConsumeFirstSave() => Interlocked.Increment(ref _saves) == 1;
    }

    private sealed class OneShotConflictStore : ISagaStore<OrderFulfillmentSaga, OrderId>
    {
        private readonly ISagaStore<OrderFulfillmentSaga, OrderId> _inner;
        private readonly SharedAttemptCounter _counter;
        private readonly string _keyPrefix;

        public OneShotConflictStore(ISagaStore<OrderFulfillmentSaga, OrderId> inner, SharedAttemptCounter counter, string keyPrefix)
        { _inner = inner; _counter = counter; _keyPrefix = keyPrefix; }

        public ValueTask<OrderFulfillmentSaga?> TryLoadAsync(OrderId key, CancellationToken ct)
            => _inner.TryLoadAsync(key, ct);
        public ValueTask<OrderFulfillmentSaga> LoadOrCreateAsync(OrderId key, CancellationToken ct)
            => _inner.LoadOrCreateAsync(key, ct);
        public ValueTask SaveAsync(OrderId key, OrderFulfillmentSaga saga, CancellationToken ct)
        {
            if (_counter.TryConsumeFirstSave())
                throw new RedisSagaConcurrencyException($"{_keyPrefix}:OrderFulfillmentSaga:{key}");
            return _inner.SaveAsync(key, saga, ct);
        }
        public ValueTask RemoveAsync(OrderId key, CancellationToken ct) => _inner.RemoveAsync(key, ct);
    }
}
