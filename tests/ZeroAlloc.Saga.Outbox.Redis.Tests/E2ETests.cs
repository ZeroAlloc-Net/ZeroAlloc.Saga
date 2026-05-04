using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga.Outbox.Redis.Tests.Fixtures;
using ZeroAlloc.Saga.Redis;

namespace ZeroAlloc.Saga.Outbox.Redis.Tests;

/// <summary>
/// End-to-end atomic-dispatch tests for the Redis-native outbox bridge.
/// Verifies the load-bearing claim of Phase 3a-2: a saga step's outbox-row
/// write commits in the same Redis MULTI/EXEC as the saga state save, so
/// rollback discards both.
/// </summary>
public sealed class E2ETests : IAsyncLifetime
{
    private readonly RedisFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public ValueTask DisposeAsync() => _fx.DisposeAsync();
    Task IAsyncLifetime.DisposeAsync() => _fx.DisposeAsync().AsTask();

    private (IServiceProvider sp, string outboxPrefix) BuildHost(Action<IServiceCollection>? extra = null)
    {
        SagaStoreRegistrar.Reset();
        var outboxPrefix = $"saga-outbox-{Guid.NewGuid():N}";
        var sagaPrefix = $"saga-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddSingleton(_fx.Multiplexer);
        services.AddTestSerializers();
        services.AddSaga()
            .WithRedisStore(opts =>
            {
                opts.MaxRetryAttempts = 3;
                opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                opts.UseExponentialBackoff = false;
                opts.KeyPrefix = sagaPrefix;
            })
            .WithOutbox()
            .WithRedisOutbox(opts => opts.KeyPrefix = outboxPrefix)
            .WithOrderFulfillmentSaga();
        extra?.Invoke(services);
        return (services.BuildServiceProvider(), outboxPrefix);
    }

    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        using var scope = sp.CreateScope();
        foreach (var h in scope.ServiceProvider.GetServices<INotificationHandler<T>>())
            await h.Handle(evt, default).ConfigureAwait(false);
    }

    [Fact]
    public async Task AtomicCommit_SagaState_AndOutboxRow_BothPersistedTogether()
    {
        var (sp, prefix) = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7001);
        await PublishAsync(sp, new OrderPlaced(orderId, 199m));

        // After the handler completes: exactly one outbox row in Redis Pending sorted set.
        var db = _fx.Multiplexer.GetDatabase();
        var pending = await db.SortedSetRangeByRankWithScoresAsync($"{prefix}:pending");
        Assert.Single(pending);

        var entryKey = $"{prefix}:entry:{(string)pending[0].Element!}";
        var typeName = (string?)await db.HashGetAsync(entryKey, "typeName");
        Assert.Equal(typeof(ReserveStockCommand).FullName, typeName);

        // Mediator NOT called on the dispatch path — the bridge enlists, doesn't dispatch inline.
        Assert.Empty(ledger.CommandsOfType<ReserveStockCommand>());

        // Drive a poll cycle: the poller dispatches via SagaCommandRegistry → mediator → ledger.
        var poller = sp.GetServices<IHostedService>().OfType<OutboxSagaCommandPoller>().Single();
        await poller.PollOnceAsync(default);

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
#pragma warning restore HLQ005

        // Pending now empty; succeeded set has the entry.
        Assert.Empty(await db.SortedSetRangeByRankAsync($"{prefix}:pending"));
        var succeeded = await db.SetMembersAsync($"{prefix}:succeeded");
        Assert.Single(succeeded);
    }

    [Fact]
    public async Task AtomicRollback_OccConflict_DiscardsOutboxRow()
    {
        // THE load-bearing test for stage 3. With WithRedisOutbox(), the outbox-row
        // write joins the saga store's MULTI/EXEC. A failed save MUST take the outbox
        // write down with it — no orphan dispatch row.
        var counter = new SharedAttemptCounter();
        var (sp, prefix) = BuildHost(services =>
        {
            // Decorate the saga store with a one-shot conflict thrower. The shared
            // counter spans per-attempt scopes (the generator's retry loop creates
            // a new IServiceScope per attempt).
            for (int i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(ISagaStore<OrderFulfillmentSaga, OrderId>))
                    services.RemoveAt(i);
            }
            services.AddScoped<ISagaStore<OrderFulfillmentSaga, OrderId>>(s =>
            {
                var inner = ActivatorUtilities.CreateInstance<RedisSagaStore<OrderFulfillmentSaga, OrderId>>(s);
                return new OneShotConflictStore(inner, counter);
            });
        });

        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7002);
        await PublishAsync(sp, new OrderPlaced(orderId, 42m));

        // CRITICAL: exactly ONE outbox row even though the saga handler made TWO
        // attempts (the first conflict, the second succeeded). The first attempt's
        // enlisted writes were discarded with its disposed scope.
        var db = _fx.Multiplexer.GetDatabase();
        var pending = await db.SortedSetRangeByRankWithScoresAsync($"{prefix}:pending");
        Assert.Single(pending);

        // Drive the poller — exactly one dispatch.
        var poller = sp.GetServices<IHostedService>().OfType<OutboxSagaCommandPoller>().Single();
        await poller.PollOnceAsync(default);

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
#pragma warning restore HLQ005
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

        public OneShotConflictStore(ISagaStore<OrderFulfillmentSaga, OrderId> inner, SharedAttemptCounter counter)
        { _inner = inner; _counter = counter; }

        public ValueTask<OrderFulfillmentSaga?> TryLoadAsync(OrderId key, CancellationToken ct) => _inner.TryLoadAsync(key, ct);
        public ValueTask<OrderFulfillmentSaga> LoadOrCreateAsync(OrderId key, CancellationToken ct) => _inner.LoadOrCreateAsync(key, ct);
        public ValueTask SaveAsync(OrderId key, OrderFulfillmentSaga saga, CancellationToken ct)
        {
            if (_counter.TryConsumeFirstSave())
                throw new RedisSagaConcurrencyException($"saga-test:{key}");
            return _inner.SaveAsync(key, saga, ct);
        }
        public ValueTask RemoveAsync(OrderId key, CancellationToken ct) => _inner.RemoveAsync(key, ct);
    }
}
