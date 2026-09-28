using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Saga.EfCore;
using ZeroAlloc.Saga.MultiAssembly.Billing;
using ZeroAlloc.Saga.MultiAssembly.Shipping;
using ZeroAlloc.Saga.Outbox;

namespace ZeroAlloc.Saga.MultiAssembly.Tests;

/// <summary>
/// Sagas declared in two assemblies, #176. Billing and Shipping each carry their own generated
/// MediatorSagaCommandDispatcher and SagaCommandRegistry. Every saga command has to be dispatched,
/// whichever assembly declares it: through the default Mediator dispatcher, and through
/// ZeroAlloc.Outbox's worker with <c>WithOutbox()</c>.
/// </summary>
public sealed class MultiAssemblySagaTests
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefaultDispatcher_DispatchesTheCommandsOfBothAssemblies(bool billingFirst)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBillingFixture().AddShippingFixture();
        var builder = services.AddSaga();
        if (billingFirst)
            builder.WithInvoiceSaga().WithShipmentSaga();
        else
            builder.WithShipmentSaga().WithInvoiceSaga();

        await using var sp = services.BuildServiceProvider();
        var billing = BillingLedger.Current = new BillingLedger();
        var shipping = ShippingLedger.Current = new ShippingLedger();

        await BillingFixture.PublishAsync(sp, new InvoiceRequested(new InvoiceId(1)));
        await ShippingFixture.PublishAsync(sp, new ShipmentRequested(new ShipmentId(2)));

        Assert.Equal(new InvoiceId(1), Assert.Single(billing.CommandsOfType<IssueInvoiceCommand>()).Id);
        Assert.Equal(new ShipmentId(2), Assert.Single(shipping.CommandsOfType<BookCarrierCommand>()).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Outbox_WorkerDispatchesTheCommandsOfBothAssemblies_NoneDeadLettered(bool outboxFirst)
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var counter = new OutboxOutcomeCounter();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IOutboxDashboardEventPublisher>(counter);
                services.AddDbContext<MultiAssemblyDbContext>(o => o.UseSqlite(fx.Connection));
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
                    .WithEfCore<MultiAssemblyDbContext>();
                services.AddBillingFixture().AddShippingFixture();
                var builder = services.AddSaga().WithEfCoreStore<MultiAssemblyDbContext>();
                if (outboxFirst)
                    builder.WithOutbox().WithInvoiceSaga().WithShipmentSaga();
                else
                    builder.WithInvoiceSaga().WithShipmentSaga().WithOutbox();
            })
            .Build();
        var billing = BillingLedger.Current = new BillingLedger();
        var shipping = ShippingLedger.Current = new ShippingLedger();

        // Each saga runs to completion: its first step saves the saga row, and its last step removes
        // it. Both commit the step's outbox row, #194.
        await BillingFixture.PublishAsync(host.Services, new InvoiceRequested(new InvoiceId(11)));
        await ShippingFixture.PublishAsync(host.Services, new ShipmentRequested(new ShipmentId(12)));
        await BillingFixture.PublishAsync(host.Services, new InvoicePaid(new InvoiceId(11)));
        await ShippingFixture.PublishAsync(host.Services, new ShipmentDelivered(new ShipmentId(12)));

        await host.StartAsync();
        try
        {
            var deadline = DateTime.UtcNow + WorkerTimeout;
            while (counter.Finished < 4)
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"The outbox worker did not finish all four rows within {WorkerTimeout.TotalSeconds} seconds: {counter.Dispatched} dispatched, {counter.DeadLettered} dead-lettered.");
                await Task.Delay(20);
            }
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.True(counter.DeadLettered == 0, $"A saga command was dead-lettered: {counter.LastDeadLetterError}");
        Assert.Equal(new InvoiceId(11), Assert.Single(billing.CommandsOfType<IssueInvoiceCommand>()).Id);
        Assert.Equal(new ShipmentId(12), Assert.Single(shipping.CommandsOfType<BookCarrierCommand>()).Id);
        Assert.Equal(new InvoiceId(11), Assert.Single(billing.CommandsOfType<CloseInvoiceCommand>()).Id);
        Assert.Equal(new ShipmentId(12), Assert.Single(shipping.CommandsOfType<CloseShipmentCommand>()).Id);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MultiAssemblyDbContext>();
        Assert.Equal(0, await context.Set<SagaInstanceEntity>().AsNoTracking().CountAsync());
        var rows = await context.Set<OutboxMessageEntity>().AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(OutboxMessageStatus.Succeeded, row.Status));
    }
}
