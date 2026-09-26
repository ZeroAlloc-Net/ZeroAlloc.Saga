using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Saga.EfCore;
using ZeroAlloc.Saga.Outbox.Tests.Fixtures;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox.Tests;

/// <summary>
/// End-to-end tests for the saga + outbox bridge on the documented EF setup. The host registers
/// <see cref="EfCoreSagaStore{TSaga,TKey}"/> AND ZeroAlloc.Outbox's EF store, sharing a single
/// scoped <see cref="OutboxE2EDbContext"/>, and ZeroAlloc.Outbox's <see cref="OutboxWorkerService"/>
/// dispatches the saga commands. The tests publish saga events to verify atomic dispatch, where
/// saga state and outbox row commit in one <c>SaveChangesAsync</c>. They also cover the OCC-conflict
/// regression caveat: Saga 1.1's duplicate dispatch on retry no longer occurs, because the losing
/// attempt's outbox row Add is discarded with the failing DbContext.
/// </summary>
/// <remarks>
/// The fixture's single SqliteConnection is not thread-safe, so a test touches the database only
/// while the worker is stopped.
/// </remarks>
public sealed class E2ETests
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(15);

    private static IHost BuildHost(SqliteFixture fx, Action<IServiceCollection>? extra = null)
    {
        // Reset process-wide registrar state between tests so the typed
        // registrar from a previous test (in EfCore.Tests' E2E suite running
        // in another assembly) doesn't bleed into this one.
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
                services.AddDbContext<OutboxE2EDbContext>(opts => opts.UseSqlite(fx.Connection),
                    ServiceLifetime.Scoped);
                // The documented setup. Outbox's worker dispatches saga commands, and its EF store
                // takes the same scoped DbContext as the saga store, which is what makes saga
                // state save + outbox row commit atomic.
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
                    .WithEfCore<OutboxE2EDbContext>();
                // Per-command JSON serializers consumed by both the OutboxSagaCommandDispatcher
                // (write path) and the generator-emitted SagaCommandRegistry (dispatch path).
                services.AddTestSerializers();
                services.AddSaga()
                    .WithEfCoreStore<OutboxE2EDbContext>(opts =>
                    {
                        opts.MaxRetryAttempts = 3;
                        opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                        opts.UseExponentialBackoff = false;
                    })
                    .WithOutbox()
                    .WithOrderFulfillmentSaga();
                // Apply test-supplied overrides AFTER per-saga registrations so
                // decorators replacing ISagaStore<> see the full registration in place.
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

    /// <summary>
    /// Starts the host, which runs WithOutbox()'s startup check and then Outbox's worker, waits
    /// until the worker has published <paramref name="expected"/> MessageDispatchedEvents, and
    /// stops the host again. The worker publishes that event only after MarkSucceededAsync, so
    /// stopping never cancels a mark in flight. Set CommandLedger.Current before calling it: the
    /// worker's execution context flows from StartAsync.
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

    [Fact]
    public async Task Saga_DispatchesViaOutbox_CommittedAtomically_WithStateSave()
    {
        // Prove that publishing OrderPlaced through the saga handler results in a single saga row
        // AND a single outbox row in the database (atomic commit), and that Outbox's worker drains
        // the outbox row to dispatch ReserveStockCommand exactly once via the mediator.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        using var host = BuildHost(fx);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7001);
        await PublishAsync(host.Services, new OrderPlaced(orderId, 199m));

        // After the handler completes: exactly one saga row and one outbox row,
        // committed by the same SaveChangesAsync (atomicity).
        using (var scope = host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<OutboxE2EDbContext>();
            var sagas = await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync();
            var outboxes = await ctx.Set<OutboxMessageEntity>().AsNoTracking().ToListAsync();
#pragma warning disable HLQ005
            Assert.Single(sagas);
            Assert.Single(outboxes);
#pragma warning restore HLQ005
            Assert.Equal(typeof(ReserveStockCommand).FullName, outboxes[0].TypeName);
            Assert.Equal(OutboxMessageStatus.Pending, outboxes[0].Status);
        }

        // The outbox-bridged dispatcher writes to the outbox INSTEAD of invoking
        // the mediator inline — so the ledger has nothing yet.
        Assert.Empty(ledger.CommandsOfType<ReserveStockCommand>());

        // Run Outbox's worker: it picks up the row, dispatches it through the saga's
        // IOutboxTypeDispatcher and the generator-emitted SagaCommandRegistry, and marks it
        // succeeded.
        await RunWorkerUntilDispatchedAsync(host, expected: 1);

        // Now ReserveStockCommand has been dispatched exactly once.
#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
#pragma warning restore HLQ005
        Assert.Equal(orderId, ledger.CommandsOfType<ReserveStockCommand>()[0].OrderId);
        Assert.Equal(199m, ledger.CommandsOfType<ReserveStockCommand>()[0].Total);

        // The worker marks the row Succeeded (not removed — EfCoreOutboxStore
        // sets Status = Succeeded on MarkSucceededAsync).
        using (var scope = host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<OutboxE2EDbContext>();
            var outboxes = await ctx.Set<OutboxMessageEntity>().AsNoTracking().ToListAsync();
#pragma warning disable HLQ005
            Assert.Single(outboxes);
#pragma warning restore HLQ005
            Assert.Equal(OutboxMessageStatus.Succeeded, outboxes[0].Status);
        }
    }

    [Fact]
    public async Task OccConflict_RollsBackOutboxRow_NoDuplicateDispatch()
    {
        // THE load-bearing regression test for Phase 3a + scope-per-attempt.
        //
        // Pre-Phase-3a: a saga handler's OCC retry would call mediator.Send
        // BEFORE state save, so a save that fails after dispatch already had
        // the side effect — at-least-once dispatch was unavoidable.
        //
        // Post-Phase-3a + scope-per-attempt: each retry attempt runs in its
        // own DI scope. The dispatcher Adds a tracked outbox row to the
        // attempt's scoped DbContext. If SaveChangesAsync throws
        // DbUpdateConcurrencyException, the attempt's `using` scope is
        // disposed — the failed DbContext (and its tracked outbox row) is
        // gone. The handler retries in a fresh scope with a fresh DbContext.
        // The successful attempt commits exactly ONE outbox row, the worker
        // drains exactly ONE entry, and ReserveStockCommand is dispatched
        // exactly ONCE.
        //
        // The OneShotConflictStore decorator below uses a SHARED counter
        // (lifted out of the per-scope wrapper) so only the FIRST physical
        // save throws — subsequent retry attempts in fresh scopes see a
        // clean inner store. NO ChangeTracker.Clear emulation: this is the
        // real architectural mechanism, not a single-process simulation.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();

        var counter = new SharedAttemptCounter();
        using var host = BuildHost(fx, services =>
        {
            for (int i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(ISagaStore<OrderFulfillmentSaga, OrderId>))
                {
                    services.RemoveAt(i);
                }
            }
            services.AddScoped<ISagaStore<OrderFulfillmentSaga, OrderId>>(s =>
            {
                var ctx = s.GetRequiredService<OutboxE2EDbContext>();
                var inner = new EfCoreSagaStore<OrderFulfillmentSaga, OrderId>(
                    ctx,
                    NullLogger<EfCoreSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);
                return new OneShotConflictStore(inner, counter);
            });
        });
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7002);
        // Should NOT throw — the retry loop catches the simulated conflict and
        // succeeds on the second attempt (in a fresh scope).
        await PublishAsync(host.Services, new OrderPlaced(orderId, 42m));

        // CRITICAL ASSERTION: exactly ONE outbox row in the database. The
        // failed first-attempt scope was disposed before its SaveChangesAsync
        // succeeded, so its tracked outbox row Add never made it to the DB.
        // Only the successful retry's outbox row is committed.
        using (var scope = host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<OutboxE2EDbContext>();
            var outboxes = await ctx.Set<OutboxMessageEntity>().AsNoTracking().ToListAsync();
#pragma warning disable HLQ005
            Assert.Single(outboxes);
            Assert.Single(await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync());
#pragma warning restore HLQ005
            Assert.Equal(typeof(ReserveStockCommand).FullName, outboxes[0].TypeName);
        }

        // Run the worker. With exactly one outbox row, ReserveStockCommand
        // is dispatched exactly once.
        await RunWorkerUntilDispatchedAsync(host, expected: 1);

        var dispatched = ledger.CommandsOfType<ReserveStockCommand>();
#pragma warning disable HLQ005
        Assert.Single(dispatched);
#pragma warning restore HLQ005
        Assert.Equal(orderId, dispatched[0].OrderId);
    }

    [Fact]
    public async Task DocumentedSetup_WorkerDispatchesEverySagaCommand_NoneDeadLettered()
    {
        // Regression for #173. With AddOutbox + WithOutbox, Saga used to run its own poller next
        // to Outbox's worker. The worker had no dispatcher for saga command types, so it
        // dead-lettered every saga row it claimed first as "No dispatcher for type".
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        using var host = BuildHost(fx);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        const int orders = 5;
        for (var i = 0; i < orders; i++)
            await PublishAsync(host.Services, new OrderPlaced(new OrderId(7100 + i), 10m + i));

        await RunWorkerUntilDispatchedAsync(host, expected: orders);

        Assert.Equal(orders, ledger.CommandsOfType<ReserveStockCommand>().Count);
        using var scope = host.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<OutboxE2EDbContext>()
            .Set<OutboxMessageEntity>().AsNoTracking().ToListAsync();
        Assert.Equal(orders, rows.Count);
        Assert.All(rows, row => Assert.Equal(OutboxMessageStatus.Succeeded, row.Status));
    }

    [Fact]
    public async Task WorkerDispatch_SharesTheStoresScopedDbContext()
    {
        // The worker resolves the store and every dispatcher from one per-batch scope. A saga
        // command must be deserialised and dispatched in that scope, so it sees the DbContext the
        // store claims and marks through, exactly as the old poller did.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var probe = new ScopeProbe();
        using var host = BuildHost(fx, services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IOutboxStore>(sp =>
            {
                probe.RecordStore(sp.GetRequiredService<OutboxE2EDbContext>());
                return sp.GetRequiredService<EfCoreOutboxStore<OutboxE2EDbContext>>();
            }));
            // The registry resolves ISerializer<T> from the dispatcher's service provider, so
            // this records the DbContext of the scope the dispatch ran in.
            services.Replace(ServiceDescriptor.Scoped<ISerializer<ReserveStockCommand>>(sp =>
            {
                probe.RecordDispatch(sp.GetRequiredService<OutboxE2EDbContext>());
                return new JsonCommandSerializer<ReserveStockCommand>();
            }));
        });
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        await PublishAsync(host.Services, new OrderPlaced(new OrderId(7201), 10m));
        // Record only what happens from here on: the publish above resolved both services too.
        probe.Arm();

        await RunWorkerUntilDispatchedAsync(host, expected: 1);

        var dispatchContexts = probe.DispatchContexts.ToArray();
        Assert.NotEmpty(dispatchContexts);
        foreach (var ctx in dispatchContexts)
            Assert.Contains(probe.StoreContexts, store => ReferenceEquals(store, ctx));
    }

    /// <summary>
    /// Records the scoped DbContext instances the outbox store and the dispatch path resolved.
    /// </summary>
    private sealed class ScopeProbe
    {
        private volatile bool _armed;

        public ConcurrentBag<OutboxE2EDbContext> StoreContexts { get; } = new();
        public ConcurrentBag<OutboxE2EDbContext> DispatchContexts { get; } = new();

        public void Arm() => _armed = true;

        public void RecordStore(OutboxE2EDbContext ctx)
        {
            if (_armed) StoreContexts.Add(ctx);
        }

        public void RecordDispatch(OutboxE2EDbContext ctx)
        {
            if (_armed) DispatchContexts.Add(ctx);
        }
    }

    /// <summary>
    /// Counter shared across per-attempt scopes — lets one (and only one)
    /// physical SaveAsync throw, regardless of how many fresh
    /// <see cref="OneShotConflictStore"/> instances the scope-per-attempt
    /// retry loop instantiates.
    /// </summary>
    private sealed class SharedAttemptCounter
    {
        private int _attemptedSaves;
        public bool TryConsumeFirstSave() => Interlocked.Increment(ref _attemptedSaves) == 1;
    }

    /// <summary>
    /// Wrapper around <see cref="ISagaStore{TSaga,TKey}"/> that throws
    /// <see cref="DbUpdateConcurrencyException"/> on the first physical
    /// <c>SaveAsync</c>. The shared <see cref="SharedAttemptCounter"/> ensures
    /// only ONE save throws even though the scope-per-attempt retry loop
    /// resolves a fresh wrapper instance per attempt — exactly the behaviour
    /// of a real cross-process race where one replica wins and the other(s)
    /// see DbUpdateException.
    /// </summary>
    private sealed class OneShotConflictStore : ISagaStore<OrderFulfillmentSaga, OrderId>
    {
        private readonly ISagaStore<OrderFulfillmentSaga, OrderId> _inner;
        private readonly SharedAttemptCounter _counter;

        public OneShotConflictStore(
            ISagaStore<OrderFulfillmentSaga, OrderId> inner,
            SharedAttemptCounter counter)
        {
            _inner = inner;
            _counter = counter;
        }

        public ValueTask<OrderFulfillmentSaga?> TryLoadAsync(OrderId key, CancellationToken ct)
            => _inner.TryLoadAsync(key, ct);

        public ValueTask<OrderFulfillmentSaga> LoadOrCreateAsync(OrderId key, CancellationToken ct)
            => _inner.LoadOrCreateAsync(key, ct);

        public ValueTask SaveAsync(OrderId key, OrderFulfillmentSaga saga, CancellationToken ct)
        {
            if (_counter.TryConsumeFirstSave())
            {
                // Real OCC conflict — no ChangeTracker.Clear, no emulation. The
                // attempt's `using` scope in the generated handler will dispose
                // the DbContext when this exception unwinds, taking the tracked
                // outbox row with it. The retry loop creates a fresh inner scope
                // and the test's SharedAttemptCounter ensures the second save
                // succeeds.
                throw new EfCoreSagaConcurrencyException(
                    "OrderFulfillmentSaga", key.ToString() ?? string.Empty,
                    new DbUpdateConcurrencyException("Transient conflict (test)."));
            }
            return _inner.SaveAsync(key, saga, ct);
        }

        public ValueTask RemoveAsync(OrderId key, CancellationToken ct) => _inner.RemoveAsync(key, ct);
    }
}
