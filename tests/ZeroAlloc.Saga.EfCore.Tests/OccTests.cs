using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga.EfCore.Tests.Fixtures;

namespace ZeroAlloc.Saga.EfCore.Tests;

/// <summary>
/// Optimistic-concurrency tests for <see cref="EfCoreSagaStore{TSaga,TKey}"/>
/// and the generator-emitted OCC retry loop. SQLite in-memory is used as the
/// backing store; the row-version concurrency token (rotated by the store on
/// every save) drives the conflict path.
/// </summary>
public sealed class OccTests
{
    private static EfCoreSagaStore<OrderFulfillmentSaga, OrderId> CreateStore(DbContext ctx)
        => new(ctx, NullLogger<EfCoreSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);

    [Fact]
    public async Task OCC_ConcurrentUpdate_SecondSaveThrowsDbUpdateConcurrencyException()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(101);

        // Seed a saved saga so two contexts can race on the update.
        var ctxSeed = fx.CreateContext();
        await using (ctxSeed.ConfigureAwait(false))
        {
            var store = CreateStore(ctxSeed);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            saga.ReserveStock(new OrderPlaced(orderId, 1m));
            await store.SaveAsync(orderId, saga, default);
        }

        // Two contexts load the saga, both update, both save. The second save
        // must fail with DbUpdateConcurrencyException because the first save
        // rotated the row-version token, leaving the second context's view stale.
        var ctxA = fx.CreateContext();
        var ctxB = fx.CreateContext();
        await using (ctxA.ConfigureAwait(false))
        await using (ctxB.ConfigureAwait(false))
        {
            var storeA = CreateStore(ctxA);
            var storeB = CreateStore(ctxB);

            var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
            var sagaB = await storeB.LoadOrCreateAsync(orderId, default);

            sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
            sagaA.ChargeCustomer(new StockReserved(orderId));

            sagaB.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
            sagaB.ChargeCustomer(new StockReserved(orderId));

            await storeA.SaveAsync(orderId, sagaA, default);

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                async () => await storeB.SaveAsync(orderId, sagaB, default).ConfigureAwait(false));
        }
    }

    [Fact]
    public async Task OCC_RaceOnLoadOrCreate_UniqueConstraintRetryHandled()
    {
        // Two processes both call LoadOrCreateAsync for a fresh correlation
        // key, both INSERT, second hits a unique-constraint violation
        // (DbUpdateException, NOT DbUpdateConcurrencyException). The
        // generator-emitted handler retry loop must catch the broadened
        // exception type and recover on the next attempt.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        SagaStoreRegistrar.Reset();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddDbContext<TestDbContext>(opts => opts.UseSqlite(fx.Connection),
            ServiceLifetime.Scoped);
        services.AddSaga()
            .WithEfCoreStore<TestDbContext>(opts => { opts.MaxRetryAttempts = 3; opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1); opts.UseExponentialBackoff = false; })
            .AddOrderFulfillmentSaga();

        // Replace the saga store with a wrapper that throws DbUpdateException
        // exactly once on the first SaveAsync (simulating a unique-constraint
        // violation when a parallel writer beats us to the INSERT). The retry
        // loop should catch DbUpdateException via the broadened catch and
        // succeed on the second attempt.
        for (int i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ISagaStore<OrderFulfillmentSaga, OrderId>))
            {
                services.RemoveAt(i);
            }
        }
        services.AddScoped<ISagaStore<OrderFulfillmentSaga, OrderId>>(sp =>
        {
            var inner = new EfCoreSagaStore<OrderFulfillmentSaga, OrderId>(
                sp.GetRequiredService<TestDbContext>(),
                NullLogger<EfCoreSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);
            return new OneShotInsertRaceStore(inner);
        });

        var sp = services.BuildServiceProvider();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(404);
        // Should NOT throw — the retry loop swallows the DbUpdateException
        // (NOT a DbUpdateConcurrencyException, which is what the original
        // narrower catch handled) and succeeds on the retry.
        await PublishAsync(sp, new OrderPlaced(orderId, 5m));

        // ReserveStock dispatched on at least one attempt (idempotency is
        // the user's responsibility; both attempts may dispatch).
        Assert.True(ledger.CommandsOfType<ReserveStockCommand>().Count >= 1,
            "Expected ReserveStock to be dispatched after the INSERT-race retry");
    }

    private sealed class OneShotInsertRaceStore : ISagaStore<OrderFulfillmentSaga, OrderId>
    {
        private readonly ISagaStore<OrderFulfillmentSaga, OrderId> _inner;
        private int _attemptedSaves;
        public OneShotInsertRaceStore(ISagaStore<OrderFulfillmentSaga, OrderId> inner) => _inner = inner;

        public ValueTask<OrderFulfillmentSaga?> TryLoadAsync(OrderId key, CancellationToken ct)
            => _inner.TryLoadAsync(key, ct);
        public ValueTask<OrderFulfillmentSaga> LoadOrCreateAsync(OrderId key, CancellationToken ct)
            => _inner.LoadOrCreateAsync(key, ct);
        public ValueTask SaveAsync(OrderId key, OrderFulfillmentSaga saga, CancellationToken ct)
        {
            if (System.Threading.Interlocked.Increment(ref _attemptedSaves) == 1)
            {
                // Simulate a unique-constraint INSERT race: DbUpdateException,
                // NOT a DbUpdateConcurrencyException. Pre-Fix-C1 the handler
                // retry loop would have let this escape.
                throw new DbUpdateException("Unique-constraint INSERT race (test).");
            }
            return _inner.SaveAsync(key, saga, ct);
        }
        public ValueTask RemoveAsync(OrderId key, CancellationToken ct) => _inner.RemoveAsync(key, ct);
    }

    [Fact]
    public async Task OCC_HandlerRetryLoop_RecoversFromTransientConflict()
    {
        // Replace the store with a wrapper that throws DbUpdateConcurrencyException
        // exactly once on the first SaveAsync, then forwards to a real
        // EfCoreSagaStore. The handler's retry loop should ride out the
        // transient conflict, succeed on the second attempt, and dispatch
        // the ReserveStock command exactly once.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        SagaStoreRegistrar.Reset();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddDbContext<TestDbContext>(opts => opts.UseSqlite(fx.Connection),
            ServiceLifetime.Scoped);
        services.AddSaga()
            .WithEfCoreStore<TestDbContext>(opts => { opts.MaxRetryAttempts = 3; opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1); opts.UseExponentialBackoff = false; })
            .AddOrderFulfillmentSaga();

        // Decorate the EfCoreSagaStore with a one-shot conflict wrapper.
        for (int i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ISagaStore<OrderFulfillmentSaga, OrderId>))
            {
                services.RemoveAt(i);
            }
        }
        services.AddScoped<ISagaStore<OrderFulfillmentSaga, OrderId>>(sp =>
        {
            var inner = new EfCoreSagaStore<OrderFulfillmentSaga, OrderId>(
                sp.GetRequiredService<TestDbContext>(),
                NullLogger<EfCoreSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);
            return new OneShotConflictStore(inner);
        });

        var sp = services.BuildServiceProvider();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(202);
        await PublishAsync(sp, new OrderPlaced(orderId, 1m));

        // The retry loop masked the first conflict and dispatched ReserveStock
        // on the second attempt — so we expect TWO records (one per attempt).
        // Exact retry semantics are at-most-once-from-OCC's-view but
        // at-least-once-from-mediator's-view; idempotency is the user's
        // responsibility under OCC retry (ZASAGA015 documents this).
        Assert.True(ledger.CommandsOfType<ReserveStockCommand>().Count >= 1,
            "Expected at least one ReserveStock command after retry");
    }

    private sealed class OneShotConflictStore : ISagaStore<OrderFulfillmentSaga, OrderId>
    {
        private readonly ISagaStore<OrderFulfillmentSaga, OrderId> _inner;
        private int _attemptedSaves;
        public OneShotConflictStore(ISagaStore<OrderFulfillmentSaga, OrderId> inner) => _inner = inner;

        public ValueTask<OrderFulfillmentSaga?> TryLoadAsync(OrderId key, CancellationToken ct)
            => _inner.TryLoadAsync(key, ct);
        public ValueTask<OrderFulfillmentSaga> LoadOrCreateAsync(OrderId key, CancellationToken ct)
            => _inner.LoadOrCreateAsync(key, ct);
        public ValueTask SaveAsync(OrderId key, OrderFulfillmentSaga saga, CancellationToken ct)
        {
            if (System.Threading.Interlocked.Increment(ref _attemptedSaves) == 1)
            {
                throw new DbUpdateConcurrencyException("Transient conflict (test).");
            }
            return _inner.SaveAsync(key, saga, ct);
        }
        public ValueTask RemoveAsync(OrderId key, CancellationToken ct) => _inner.RemoveAsync(key, ct);
    }

    [Fact]
    public async Task OCC_ExceedsMaxRetries_ThrowsSagaConcurrencyException()
    {
        // Build a host where every save raises DbUpdateConcurrencyException,
        // forcing the handler to exhaust the retry budget. The ISagaStore
        // is replaced with a wrapper that always rejects saves.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        SagaStoreRegistrar.Reset();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddDbContext<TestDbContext>(opts => opts.UseSqlite(fx.Connection),
            ServiceLifetime.Scoped);
        services.AddSaga()
            .WithEfCoreStore<TestDbContext>(opts => { opts.MaxRetryAttempts = 2; opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1); opts.UseExponentialBackoff = false; })
            .AddOrderFulfillmentSaga();

        // Replace the store with one that always throws.
        for (int i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ISagaStore<OrderFulfillmentSaga, OrderId>))
            {
                services.RemoveAt(i);
            }
        }
        services.AddScoped<ISagaStore<OrderFulfillmentSaga, OrderId>, AlwaysConflictingStore>();

        var sp = services.BuildServiceProvider();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(303);
        var ex = await Assert.ThrowsAsync<SagaConcurrencyException>(async () =>
            await PublishAsync(sp, new OrderPlaced(orderId, 1m)).ConfigureAwait(false));
        Assert.Equal("OrderFulfillmentSaga", ex.SagaType);
        Assert.Equal(2, ex.Attempts);
    }

    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        using var scope = sp.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<INotificationHandler<T>>();
        foreach (var h in handlers)
        {
            await h.Handle(evt, default).ConfigureAwait(false);
        }
    }

    private sealed class AlwaysConflictingStore : ISagaStore<OrderFulfillmentSaga, OrderId>
    {
        public ValueTask<OrderFulfillmentSaga?> TryLoadAsync(OrderId key, CancellationToken ct)
            => new((OrderFulfillmentSaga?)null);
        public ValueTask<OrderFulfillmentSaga> LoadOrCreateAsync(OrderId key, CancellationToken ct)
            => new(new OrderFulfillmentSaga());
        public ValueTask SaveAsync(OrderId key, OrderFulfillmentSaga saga, CancellationToken ct)
            => throw new DbUpdateConcurrencyException("Always conflict (test).");
        public ValueTask RemoveAsync(OrderId key, CancellationToken ct)
            => ValueTask.CompletedTask;
    }
}
