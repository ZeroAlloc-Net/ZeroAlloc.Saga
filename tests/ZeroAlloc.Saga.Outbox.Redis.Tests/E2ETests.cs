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

    private IServiceProvider BuildHost(string sagaPrefix, string outboxPrefix, Action<IServiceCollection>? extra = null)
    {
        SagaStoreRegistrar.Reset();
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
        return services.BuildServiceProvider();
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
        var sagaPrefix = $"saga-{Guid.NewGuid():N}";
        var outboxPrefix = $"saga-outbox-{Guid.NewGuid():N}";
        var sp = BuildHost(sagaPrefix, outboxPrefix);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7001);
        await PublishAsync(sp, new OrderPlaced(orderId, 199m));

        // After the handler completes: exactly one outbox row in Redis Pending sorted set.
        var db = _fx.Multiplexer.GetDatabase();
        var pending = await db.SortedSetRangeByRankWithScoresAsync($"{outboxPrefix}:pending");
        Assert.Single(pending);

        var entryKey = $"{outboxPrefix}:entry:{(string)pending[0].Element!}";
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
        Assert.Empty(await db.SortedSetRangeByRankAsync($"{outboxPrefix}:pending"));
        var succeeded = await db.SetMembersAsync($"{outboxPrefix}:succeeded");
        Assert.Single(succeeded);
    }

    [Fact]
    public async Task AtomicRollback_WatchConflict_Mid_MULTI_DiscardsBothSagaState_AndOutboxRow()
    {
        // THE load-bearing test for stage 3. We force a real WATCH conflict mid-MULTI:
        // a custom IRedisSagaTransactionContributor (registered alongside the outbox
        // contributor) writes to the saga's watched key via a SIBLING connection
        // BEFORE EXEC fires. Redis records the modification → EXEC returns null →
        // RedisSagaConcurrencyException → scope-per-attempt retry. The first attempt's
        // queued saga-state HSET *and* outbox-row HSET/ZADD are discarded together as
        // the MULTI batch is abandoned. Only the second attempt's commit produces a
        // pending outbox row.
        //
        // This proves the EXEC-level atomicity claim (saga state and outbox writes
        // committed-or-discarded as one), not just scope-per-attempt isolation.
        var counter = new SharedAttemptCounter();
        var orderId = new OrderId(7002);
        var sagaPrefix = $"saga-{Guid.NewGuid():N}";
        var outboxPrefix = $"saga-outbox-{Guid.NewGuid():N}";

        var sp = BuildHost(sagaPrefix, outboxPrefix, services =>
        {
            services.AddScoped<IRedisSagaTransactionContributor>(_ =>
                new WatchConflictInjector(_fx.Multiplexer.GetDatabase(), sagaPrefix, orderId, counter));
        });

        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        await PublishAsync(sp, new OrderPlaced(orderId, 42m));

        // Post-conditions:
        // 1. Conflict was injected exactly once on attempt 1 (and the contributor was
        //    invoked at least twice — once per attempt — proving the saga store retried).
        Assert.Equal(1, counter.ConflictsInjected);
        Assert.True(counter.Attempts >= 2, $"expected ≥2 attempts, got {counter.Attempts}");

        // 2. Exactly ONE outbox row in pending. Attempt 1's row was inside the
        //    aborted MULTI; attempt 2's row is the only one that committed.
        var db = _fx.Multiplexer.GetDatabase();
        var pending = await db.SortedSetRangeByRankWithScoresAsync($"{outboxPrefix}:pending");
        Assert.Single(pending);

        // 3. Saga state matches attempt 2's commit. The injector's transient HSET was
        //    DEL'd before returning so attempt 2 saw a clean key, then committed normally.
        var sagaKey = $"{sagaPrefix}:OrderFulfillmentSaga:{orderId}";
        var version = (string?)await db.HashGetAsync(sagaKey, "version");
        Assert.NotNull(version);
        Assert.NotEqual("watch-conflict-injected", version);

        // 4. Driving the poller dispatches exactly once.
        var poller = sp.GetServices<IHostedService>().OfType<OutboxSagaCommandPoller>().Single();
        await poller.PollOnceAsync(default);

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
#pragma warning restore HLQ005
    }

    [Fact]
    public void Dispatcher_And_Contributor_Resolve_Same_UnitOfWork_Instance()
    {
        // Load-bearing invariant: the dispatcher resolves ISagaUnitOfWork to enlist
        // outbox-row writes; the contributor resolves RedisSagaUnitOfWork to drain
        // them inside the saga store's MULTI. If these were two different instances
        // (e.g. via accidental TryAddScoped registration ordering), enlisted writes
        // would go to a buffer the contributor never sees, silently breaking atomicity.
        // This regression test guards against any future change to the WithRedisOutbox
        // alias-via-factory pattern.
        var sp = BuildHost($"saga-{Guid.NewGuid():N}", $"saga-outbox-{Guid.NewGuid():N}");
        using var scope = sp.CreateScope();
        var asUow = scope.ServiceProvider.GetRequiredService<ISagaUnitOfWork>();
        var asConcrete = scope.ServiceProvider.GetRequiredService<RedisSagaUnitOfWork>();
        Assert.Same(asConcrete, asUow);
    }

    [Fact]
    public async Task CrossReplica_NoDuplicateOutboxRow_ForSameSagaCorrelation()
    {
        // The cross-process race claim from docs/outbox-redis.md. Two replicas
        // (separate IServiceProviders, separate scopes, but the same Redis instance)
        // process the same OrderPlaced. The saga store's WATCH/MULTI/EXEC OCC ensures
        // at most one replica's MULTI commits the OrderPlaced step; the other reloads
        // and observes the existing FSM state, so its OrderPlaced TryFire returns false
        // (the trigger isn't valid past Initial). Either way: a single outbox row in
        // pending — no duplicates across replicas.
        //
        // This is a serialized two-replica scenario. The WATCH-abort-mid-MULTI
        // mechanism itself is exercised by AtomicRollback_WatchConflict_Mid_MULTI...
        var sagaPrefix = $"shared-saga-{Guid.NewGuid():N}";
        var outboxPrefix = $"shared-outbox-{Guid.NewGuid():N}";
        var spA = BuildHost(sagaPrefix, outboxPrefix);
        var spB = BuildHost(sagaPrefix, outboxPrefix);

        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7003);
        await PublishAsync(spA, new OrderPlaced(orderId, 333m));
        await PublishAsync(spB, new OrderPlaced(orderId, 333m));

        // Replica B's publish reloaded the saga A wrote. The FSM was already past
        // Initial → TryFire(OrderPlaced) returned false → no second outbox row.
        var db = _fx.Multiplexer.GetDatabase();
        var pending = await db.SortedSetRangeByRankWithScoresAsync($"{outboxPrefix}:pending");
        Assert.Single(pending);
    }

    private sealed class SharedAttemptCounter
    {
        private int _attempts;
        private int _conflictsInjected;
        public int Attempts => Volatile.Read(ref _attempts);
        public int ConflictsInjected => Volatile.Read(ref _conflictsInjected);
        public bool TryConsumeFirst()
        {
            var n = Interlocked.Increment(ref _attempts);
            if (n != 1) return false;
            Interlocked.Increment(ref _conflictsInjected);
            return true;
        }
    }

    /// <summary>
    /// Test-only contributor that triggers a real WATCH abort on its first invocation.
    /// Writes to the saga's watched key via a sibling Redis connection, then immediately
    /// deletes the key so the next attempt's TryLoad sees a clean slate. Redis records
    /// the (transient) modification, so EXEC returns null → RedisSagaConcurrencyException.
    /// </summary>
    private sealed class WatchConflictInjector : IRedisSagaTransactionContributor
    {
        private readonly IDatabase _siblingDb;
        private readonly string _sagaPrefix;
        private readonly OrderId _targetKey;
        private readonly SharedAttemptCounter _counter;

        public WatchConflictInjector(IDatabase siblingDb, string sagaPrefix, OrderId targetKey, SharedAttemptCounter counter)
        {
            _siblingDb = siblingDb;
            _sagaPrefix = sagaPrefix;
            _targetKey = targetKey;
            _counter = counter;
        }

        public void Contribute(ITransaction transaction)
        {
            if (!_counter.TryConsumeFirst()) return;
            var sagaKey = $"{_sagaPrefix}:OrderFulfillmentSaga:{_targetKey}";
            // Modify (creates) and then immediately delete the watched key. Redis records
            // the modification regardless of net change — EXEC will abort. The DEL ensures
            // attempt 2's TryLoad observes a non-existent key (fresh saga), not a malformed one.
            _siblingDb.HashSet(sagaKey, "version", "watch-conflict-injected");
            _siblingDb.KeyDelete(sagaKey);
        }
    }
}
