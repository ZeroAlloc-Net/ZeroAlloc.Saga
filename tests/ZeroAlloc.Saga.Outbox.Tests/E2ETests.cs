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
}
