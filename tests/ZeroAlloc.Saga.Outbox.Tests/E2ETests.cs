using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Saga.EfCore;
using ZeroAlloc.Saga.Outbox.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Tests;

/// <summary>
/// End-to-end tests for the saga + outbox bridge. Each test wires a host with
/// <see cref="EfCoreSagaStore{TSaga,TKey}"/> AND <see cref="EfCoreOutboxStore{TContext}"/>
/// sharing a single scoped <see cref="OutboxE2EDbContext"/>, then publishes saga
/// events to verify atomic dispatch (saga state + outbox row commit in one
/// <c>SaveChangesAsync</c>) and the OCC-conflict regression caveat (Saga 1.1's
/// duplicate-dispatch on retry no longer occurs because the losing attempt's
/// outbox row Add is discarded with the failing DbContext).
/// </summary>
public sealed class E2ETests
{
    private static IServiceProvider BuildHost(SqliteFixture fx, Action<IServiceCollection>? extra = null)
    {
        // Reset process-wide registrar state between tests so the typed
        // registrar from a previous test (in EfCore.Tests' E2E suite running
        // in another assembly) doesn't bleed into this one.
        SagaStoreRegistrar.Reset();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddDbContext<OutboxE2EDbContext>(opts => opts.UseSqlite(fx.Connection),
            ServiceLifetime.Scoped);
        // EfCoreOutboxStore takes the same scoped DbContext — this is the
        // shared-DbContext that makes saga state save + outbox row commit atomic.
        services.AddScoped<IOutboxStore, EfCoreOutboxStore<OutboxE2EDbContext>>();
        // Per-command JSON serializers consumed by both the OutboxSagaCommandDispatcher
        // (write path) and the generator-emitted SagaCommandRegistry (poll path).
        services.AddTestSerializers();
        services.AddSaga()
            .WithEfCoreStore<OutboxE2EDbContext>(opts =>
            {
                opts.MaxRetryAttempts = 3;
                opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                opts.UseExponentialBackoff = false;
            })
            .WithOutbox()
            .AddOrderFulfillmentSaga();
        // Apply test-supplied overrides AFTER per-saga registrations so
        // decorators replacing ISagaStore<> see the full registration in place.
        extra?.Invoke(services);
        return services.BuildServiceProvider();
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

    [Fact]
    public async Task Saga_DispatchesViaOutbox_CommittedAtomically_WithStateSave()
    {
        // Task 14: prove that publishing OrderPlaced through the saga handler
        // results in a single saga row AND a single outbox row in the database
        // (atomic commit), and that the poller drains the outbox row to dispatch
        // ReserveStockCommand exactly once via the mediator.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var sp = BuildHost(fx);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(7001);
        await PublishAsync(sp, new OrderPlaced(orderId, 199m));

        // After the handler completes: exactly one saga row and one outbox row,
        // committed by the same SaveChangesAsync (atomicity).
        using (var scope = sp.CreateScope())
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

        // Drive a single poll cycle: the poller fetches the pending entry,
        // dispatches it via the generator-emitted SagaCommandRegistry, and
        // marks it succeeded.
        var poller = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<OutboxSagaCommandPoller>()
            .Single();
        await poller.PollOnceAsync(default);

        // Now ReserveStockCommand has been dispatched exactly once.
#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
#pragma warning restore HLQ005
        Assert.Equal(orderId, ledger.CommandsOfType<ReserveStockCommand>()[0].OrderId);
        Assert.Equal(199m, ledger.CommandsOfType<ReserveStockCommand>()[0].Total);

        // The poller marks the row Succeeded (not removed — EfCoreOutboxStore
        // sets Status = Succeeded on MarkSucceededAsync).
        using (var scope = sp.CreateScope())
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
        // The successful attempt commits exactly ONE outbox row, the poller
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
        var sp = BuildHost(fx, services =>
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
        await PublishAsync(sp, new OrderPlaced(orderId, 42m));

        // CRITICAL ASSERTION: exactly ONE outbox row in the database. The
        // failed first-attempt scope was disposed before its SaveChangesAsync
        // succeeded, so its tracked outbox row Add never made it to the DB.
        // Only the successful retry's outbox row is committed.
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<OutboxE2EDbContext>();
            var outboxes = await ctx.Set<OutboxMessageEntity>().AsNoTracking().ToListAsync();
#pragma warning disable HLQ005
            Assert.Single(outboxes);
            Assert.Single(await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync());
#pragma warning restore HLQ005
            Assert.Equal(typeof(ReserveStockCommand).FullName, outboxes[0].TypeName);
        }

        // Drive the poller once. With exactly one outbox row, ReserveStockCommand
        // is dispatched exactly once.
        var poller = sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<OutboxSagaCommandPoller>()
            .Single();
        await poller.PollOnceAsync(default);

        var dispatched = ledger.CommandsOfType<ReserveStockCommand>();
#pragma warning disable HLQ005
        Assert.Single(dispatched);
#pragma warning restore HLQ005
        Assert.Equal(orderId, dispatched[0].OrderId);
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
                throw new DbUpdateConcurrencyException("Transient conflict (test).");
            }
            return _inner.SaveAsync(key, saga, ct);
        }

        public ValueTask RemoveAsync(OrderId key, CancellationToken ct) => _inner.RemoveAsync(key, ct);
    }
}
