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
/// End-to-end tests that exercise the full DI stack — generator-emitted
/// notification handlers, EfCoreSagaStore, mediator, and SQLite — to verify
/// the saga progresses correctly with the durable backend wired in.
/// </summary>
[Collection(EfCoreStaticStateCollection.Name)]
public sealed class E2ETests
{
    private static IServiceProvider BuildHost(SqliteFixture fx)
    {
        // Reset process-wide registrar state between tests so the typed
        // registrar from a previous test doesn't bleed into this one.
        SagaStoreRegistrar.Reset();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddDbContext<TestDbContext>(opts => opts.UseSqlite(fx.Connection),
            ServiceLifetime.Scoped);
        services.AddSaga()
            .WithEfCoreStore<TestDbContext>(opts => { opts.MaxRetryAttempts = 3; opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1); opts.UseExponentialBackoff = false; })
            .AddOrderFulfillmentSaga();
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
    public async Task E2E_OrderFulfillmentSaga_HappyPath_WithEfCore()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var sp = BuildHost(fx);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(1001);

        await PublishAsync(sp, new OrderPlaced(orderId, 250m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentCharged(orderId));

        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
        Assert.Single(ledger.CommandsOfType<ChargeCustomerCommand>());
        Assert.Single(ledger.CommandsOfType<ShipOrderCommand>());

        // Saga removed after final step.
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.Empty(await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task E2E_OrderFulfillmentSaga_Compensation_WithEfCore()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var sp = BuildHost(fx);
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(2002);

        await PublishAsync(sp, new OrderPlaced(orderId, 50m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentDeclined(orderId));

        // Reverse cascade: refund first, then cancel.
        var refunds = ledger.CommandsOfType<RefundPaymentCommand>();
        var cancels = ledger.CommandsOfType<CancelReservationCommand>();
        Assert.Single(refunds);
        Assert.Single(cancels);

        var idxRefund = ledger.AllCommands.ToList().IndexOf(refunds[0]);
        var idxCancel = ledger.AllCommands.ToList().IndexOf(cancels[0]);
        Assert.True(idxRefund < idxCancel, "RefundPayment should run before CancelReservation");

        // Saga row removed after compensation.
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.Empty(await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task E2E_State_PersistsAcrossProcessRestart()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(3003);

        // Phase 1: build a host, publish OrderPlaced (saga saves to DB),
        // then dispose the ServiceProvider — simulating process exit.
        {
            var sp = BuildHost(fx);
            var ledger = new CommandLedger();
            CommandLedger.Current = ledger;
            await PublishAsync(sp, new OrderPlaced(orderId, 999.99m));
            Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
            await ((IAsyncDisposable)sp).DisposeAsync();
        }

        // Phase 2: build a *new* host (fresh DI graph, fresh DbContext, but
        // shared SQLite connection holds the data) and publish StockReserved.
        // The saga should load from the DB, see Step1, advance to Step2, and
        // dispatch ChargeCustomer with the persisted Total of 999.99.
        {
            var sp2 = BuildHost(fx);
            var ledger2 = new CommandLedger();
            CommandLedger.Current = ledger2;
            await PublishAsync(sp2, new StockReserved(orderId));
            var charge = ledger2.CommandsOfType<ChargeCustomerCommand>();
            Assert.Single(charge);
            Assert.Equal(999.99m, charge[0].Total);
            await ((IAsyncDisposable)sp2).DisposeAsync();
        }
    }
}
