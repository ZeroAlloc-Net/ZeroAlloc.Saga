using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga.Outbox.Redis.Tests.Fixtures;
using ZeroAlloc.Saga.Redis;

namespace ZeroAlloc.Saga.Outbox.Redis.Tests;

/// <summary>
/// End-to-end atomic-dispatch tests for the Redis-native outbox bridge.
/// Verifies the load-bearing claim of Phase 3a-2: a saga step's outbox-row
/// write commits in the same Redis MULTI/EXEC as the saga state save, so
/// rollback discards both. ZeroAlloc.Outbox's worker dispatches the commands.
/// </summary>
public sealed class E2ETests : IAsyncLifetime
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(15);
    private readonly RedisFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public ValueTask DisposeAsync() => _fx.DisposeAsync();
    Task IAsyncLifetime.DisposeAsync() => _fx.DisposeAsync().AsTask();

    /// <summary>
    /// The database the host resolves: the plain one, or with a non-empty
    /// <paramref name="dbKeyPrefix"/> a key-prefixed one, which prefixes every key a command
    /// sends, script KEYS included, but never script ARGV.
    /// </summary>
    private IDatabase Database(string dbKeyPrefix)
        => dbKeyPrefix.Length == 0
            ? _fx.Multiplexer.GetDatabase()
            : _fx.Multiplexer.GetDatabase().WithKeyPrefix(dbKeyPrefix);

    private IHost BuildHost(string sagaPrefix, string outboxPrefix, Action<IServiceCollection>? extra = null, string dbKeyPrefix = "")
    {
        SagaStoreRegistrar.Reset();
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                // Counts the worker's MessageDispatchedEvents; RunWorkerUntilDispatchedAsync waits on it.
                services.AddSingleton<DispatchedEventCounter>();
                services.AddSingleton<IOutboxDashboardEventPublisher>(
                    sp => sp.GetRequiredService<DispatchedEventCounter>());
                services.AddMediator();
                // Mediator 4.x: explicit handler registration (no reflection).
                services.TryAddTransient<ReserveStockHandler>();
                services.TryAddTransient<ChargeCustomerHandler>();
                services.TryAddTransient<ShipOrderHandler>();
                services.TryAddTransient<CancelReservationHandler>();
                services.TryAddTransient<RefundPaymentHandler>();
                services.AddSingleton(_fx.Multiplexer);
                // WithRedisStore and WithRedisOutbox register IDatabase with TryAdd, so a
                // registration made first wins.
                if (dbKeyPrefix.Length > 0)
                    services.AddScoped(_ => Database(dbKeyPrefix));
                services.AddTestSerializers();
                services.AddWelcomeSagaFixture();
                // The documented Redis setup: AddOutbox registers the worker, and
                // WithRedisOutbox supplies the IOutboxStore.
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50));
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
                    .WithOrderFulfillmentSaga()
                    .WithWelcomeSaga();
                extra?.Invoke(services);
            })
            .Build();
    }

    // Goes through the real IMediator.Publish rather than resolving INotificationHandler<T>
    // directly — see the note in ZeroAlloc.Saga.EfCore.Tests.E2ETests. The outbox's atomicity
    // guarantee has to hold on the path consumers actually use (#127).
    private static async Task PublishAsync(IServiceProvider sp, OrderPlaced evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    // IMediator.Publish has one overload per notification type, so each event needs its own helper.
    private static async Task PublishAsync(IServiceProvider sp, StockReserved evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, PaymentCharged evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, PaymentDeclined evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, CustomerRegistered evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    /// <summary>The type names of the outbox entries still pending under <paramref name="outboxPrefix"/>.</summary>
    private async Task<string[]> PendingTypeNamesAsync(string outboxPrefix)
    {
        var db = _fx.Multiplexer.GetDatabase();
        var ids = await db.SortedSetRangeByRankAsync($"{outboxPrefix}:pending").ConfigureAwait(false);
        var names = new string[ids.Length];
        for (var i = 0; i < ids.Length; i++)
            names[i] = (string)(await db.HashGetAsync($"{outboxPrefix}:entry:{(string)ids[i]!}", "typeName").ConfigureAwait(false))!;
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// Starts the host, waits until the worker has published <paramref name="expected"/>
    /// MessageDispatchedEvents, and stops it. The worker publishes that event only after
    /// MarkSucceededAsync, so stopping never cancels a mark in flight. Set CommandLedger.Current
    /// before calling it: the worker's execution context flows from StartAsync.
    /// </summary>
    private static async Task RunWorkerUntilDispatchedAsync(IHost host, int expected)
    {
        var counter = host.Services.GetRequiredService<DispatchedEventCounter>();
        await host.StartAsync().ConfigureAwait(false);
        try
        {
            var deadline = DateTime.UtcNow + WorkerTimeout;
            while (counter.Dispatched < expected)
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"The outbox worker did not finish within {WorkerTimeout.TotalSeconds} seconds.");
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("ns:")]
    public async Task AtomicCommit_SagaState_AndOutboxRow_BothPersistedTogether(string dbKeyPrefix)
    {
        var sagaPrefix = $"saga-{Guid.NewGuid():N}";
        var outboxPrefix = $"saga-outbox-{Guid.NewGuid():N}";
        using var host = BuildHost(sagaPrefix, outboxPrefix, dbKeyPrefix: dbKeyPrefix);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7001);
        await PublishAsync(host.Services, new OrderPlaced(orderId, 199m));

        // After the handler completes: exactly one outbox row in Redis Pending sorted set.
        var db = _fx.Multiplexer.GetDatabase();
        var pending = await db.SortedSetRangeByRankWithScoresAsync($"{dbKeyPrefix}{outboxPrefix}:pending");
        Assert.Single(pending);

        var entryKey = $"{dbKeyPrefix}{outboxPrefix}:entry:{(string)pending[0].Element!}";
        var typeName = (string?)await db.HashGetAsync(entryKey, "typeName");
        Assert.Equal(typeof(ReserveStockCommand).FullName, typeName);

        // Mediator NOT called on the dispatch path — the bridge enlists, doesn't dispatch inline.
        Assert.Empty(ledger.CommandsOfType<ReserveStockCommand>());

        // Run the worker: it dispatches via the saga's dispatcher → SagaCommandRegistry → mediator → ledger.
        await RunWorkerUntilDispatchedAsync(host, expected: 1);

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
#pragma warning restore HLQ005

        // Pending now empty; succeeded set has the entry.
        Assert.Empty(await db.SortedSetRangeByRankAsync($"{dbKeyPrefix}{outboxPrefix}:pending"));
        var succeeded = await db.SetMembersAsync($"{dbKeyPrefix}{outboxPrefix}:succeeded");
        Assert.Single(succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ns:")]
    public async Task AtomicRollback_WatchConflict_Mid_MULTI_DiscardsBothSagaState_AndOutboxRow(string dbKeyPrefix)
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

        using var host = BuildHost(sagaPrefix, outboxPrefix, services =>
        {
            services.AddScoped<IRedisSagaTransactionContributor>(_ =>
                new WatchConflictInjector(Database(dbKeyPrefix), sagaPrefix, orderId, counter));
        }, dbKeyPrefix);

        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        await PublishAsync(host.Services, new OrderPlaced(orderId, 42m));

        // Post-conditions:
        // 1. Conflict was injected exactly once on attempt 1 (and the contributor was
        //    invoked at least twice — once per attempt — proving the saga store retried).
        Assert.Equal(1, counter.ConflictsInjected);
        Assert.True(counter.Attempts >= 2, $"expected ≥2 attempts, got {counter.Attempts}");

        // 2. Exactly ONE outbox row in pending. Attempt 1's row was inside the
        //    aborted MULTI; attempt 2's row is the only one that committed.
        var db = _fx.Multiplexer.GetDatabase();
        var pending = await db.SortedSetRangeByRankWithScoresAsync($"{dbKeyPrefix}{outboxPrefix}:pending");
        Assert.Single(pending);

        // 3. Saga state matches attempt 2's commit. The injector's transient HSET was
        //    DEL'd before returning so attempt 2 saw a clean key, then committed normally.
        var sagaKey = $"{dbKeyPrefix}{sagaPrefix}:OrderFulfillmentSaga:{orderId}";
        var version = (string?)await db.HashGetAsync(sagaKey, "version");
        Assert.NotNull(version);
        Assert.NotEqual("watch-conflict-injected", version, StringComparer.Ordinal);

        // 4. Running the worker dispatches exactly once.
        await RunWorkerUntilDispatchedAsync(host, expected: 1);

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
#pragma warning restore HLQ005
    }

    [Fact]
    public async Task SingleStepSaga_StartedAndCompletedByOneEvent_CommitsItsOutboxRow()
    {
        // #194. The one event both starts and completes the saga, so the handler ends in
        // RemoveAsync, not SaveAsync. RemoveAsync has to drain the transaction contributors into
        // its MULTI/EXEC too, or the enlisted outbox row is dropped with the scope.
        var sagaPrefix = $"saga-{Guid.NewGuid():N}";
        var outboxPrefix = $"saga-outbox-{Guid.NewGuid():N}";
        using var host = BuildHost(sagaPrefix, outboxPrefix);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var customerId = new CustomerId(8001);
        await PublishAsync(host.Services, new CustomerRegistered(customerId));

        Assert.Equal([typeof(SendWelcomeCommand).FullName!], await PendingTypeNamesAsync(outboxPrefix));
        var db = _fx.Multiplexer.GetDatabase();
        Assert.False(await db.KeyExistsAsync($"{sagaPrefix}:WelcomeSaga:{customerId}"));

        await RunWorkerUntilDispatchedAsync(host, expected: 1);

        Assert.Equal(customerId, Assert.Single(ledger.CommandsOfType<SendWelcomeCommand>()).CustomerId);
    }

    [Fact]
    public async Task LastStep_OfAMultiStepSaga_CommitsItsOutboxRow_AndRemovesTheSaga()
    {
        // The completing step of a saga whose key exists ends in RemoveAsync as well; the last
        // step's command must commit in the same MULTI/EXEC as the key delete.
        var sagaPrefix = $"saga-{Guid.NewGuid():N}";
        var outboxPrefix = $"saga-outbox-{Guid.NewGuid():N}";
        using var host = BuildHost(sagaPrefix, outboxPrefix);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(8101);
        await PublishAsync(host.Services, new OrderPlaced(orderId, 5m));
        await PublishAsync(host.Services, new StockReserved(orderId));
        await PublishAsync(host.Services, new PaymentCharged(orderId));

        Assert.Equal(
            [typeof(ChargeCustomerCommand).FullName!, typeof(ReserveStockCommand).FullName!, typeof(ShipOrderCommand).FullName!],
            await PendingTypeNamesAsync(outboxPrefix));
        var db = _fx.Multiplexer.GetDatabase();
        Assert.False(await db.KeyExistsAsync($"{sagaPrefix}:OrderFulfillmentSaga:{orderId}"));

        await RunWorkerUntilDispatchedAsync(host, expected: 3);

        Assert.Equal(orderId, Assert.Single(ledger.CommandsOfType<ShipOrderCommand>()).OrderId);
    }

    [Fact]
    public async Task Compensation_CommitsItsCompensationCommands_AndRemovesTheSaga()
    {
        // Compensation ends in RemoveAsync too; its compensation commands must commit with it.
        var sagaPrefix = $"saga-{Guid.NewGuid():N}";
        var outboxPrefix = $"saga-outbox-{Guid.NewGuid():N}";
        using var host = BuildHost(sagaPrefix, outboxPrefix);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(8201);
        await PublishAsync(host.Services, new OrderPlaced(orderId, 5m));
        await PublishAsync(host.Services, new StockReserved(orderId));
        await PublishAsync(host.Services, new PaymentDeclined(orderId));

        var pending = await PendingTypeNamesAsync(outboxPrefix);
        Assert.Contains(typeof(RefundPaymentCommand).FullName!, pending, StringComparer.Ordinal);
        Assert.Contains(typeof(CancelReservationCommand).FullName!, pending, StringComparer.Ordinal);
        var db = _fx.Multiplexer.GetDatabase();
        Assert.False(await db.KeyExistsAsync($"{sagaPrefix}:OrderFulfillmentSaga:{orderId}"));

        await RunWorkerUntilDispatchedAsync(host, expected: pending.Length);

        Assert.Single(ledger.CommandsOfType<RefundPaymentCommand>());
        Assert.Single(ledger.CommandsOfType<CancelReservationCommand>());
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
        using var host = BuildHost($"saga-{Guid.NewGuid():N}", $"saga-outbox-{Guid.NewGuid():N}");
        using var scope = host.Services.CreateScope();
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
        using var hostA = BuildHost(sagaPrefix, outboxPrefix);
        using var hostB = BuildHost(sagaPrefix, outboxPrefix);

        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7003);
        await PublishAsync(hostA.Services, new OrderPlaced(orderId, 333m));
        await PublishAsync(hostB.Services, new OrderPlaced(orderId, 333m));

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

    [Fact]
    public async Task Start_With_RedisStore_And_WithRedisOutbox_Succeeds()
    {
        // The supported Redis pairing: the worker claims from the RedisOutboxStore that
        // WithRedisOutbox() registers, the store the unit of work writes to. #199
        using var host = BuildHost("saga-pair-ok", "outbox-pair-ok");

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Start_With_Another_Outbox_Store_Registered_After_WithRedisOutbox_Throws()
    {
        // WithRedisOutbox() writes each saga command into the Redis outbox inside the saga
        // store's MULTI/EXEC. An IOutboxStore registered after it, such as
        // AddOutbox().WithEfCore<TContext>(), becomes the store the worker claims from, so no
        // saga command would ever be dispatched. #199
        using var host = BuildHost("saga-pair-bad", "outbox-pair-bad",
            extra: services => services.AddScoped<IOutboxStore, StrayOutboxStore>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.StartsWith("ZeroAlloc.Saga.Outbox.Redis.WithRedisOutbox(): ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("StrayOutboxStore", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RedisOutboxStore", ex.Message, StringComparison.Ordinal);
    }

    // Stands in for any other IOutboxStore; the check must reject it before the worker runs.
    private sealed class StrayOutboxStore : IOutboxStore
    {
        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<System.Collections.Generic.IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<int> ReleaseLeasesAsync(System.Collections.Generic.IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();
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
