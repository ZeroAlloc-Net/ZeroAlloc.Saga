using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga.Tests.Fixtures;

namespace ZeroAlloc.Saga.Tests;

/// <summary>
/// A saga whose events share simple names across namespaces: Warehouse.Placed and Billing.Placed
/// drive different steps, and Warehouse.Rejected and Billing.Rejected compensate different
/// steps. Each event needs its own handler and FSM trigger, #216.
/// </summary>
public class SameNamedEventsTests
{
    private static (IServiceProvider Sp, CommandLedger Ledger) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddMediator();
        services.TryAddTransient<PickHandler>();
        services.TryAddTransient<UnpickHandler>();
        services.TryAddTransient<InvoiceHandler>();
        services.TryAddTransient<VoidInvoiceHandler>();
        services.TryAddTransient<DispatchHandler>();
        services.AddSaga().WithSameNamedEventsSaga();

        var ledger = new CommandLedger(null);
        CommandLedger.Current = ledger;
        return (services.BuildServiceProvider(), ledger);
    }

    private static IMediator Mediator(IServiceProvider sp) => sp.GetRequiredService<IMediator>();

    private static ISagaManager<SameNamedEventsSaga, OrderId> Manager(IServiceProvider sp)
        => sp.GetRequiredService<ISagaManager<SameNamedEventsSaga, OrderId>>();

    [Fact]
    public async Task Each_Same_Named_Event_Drives_Its_Own_Step()
    {
        var (sp, ledger) = BuildHost();
        var id = new OrderId(1);

        await Mediator(sp).Publish(new Fixtures.Warehouse.Placed(id), default);
        Assert.Single(ledger.CommandsOfType<PickCommand>());
        Assert.Empty(ledger.CommandsOfType<InvoiceCommand>());

        await Mediator(sp).Publish(new Fixtures.Billing.Placed(id), default);
        Assert.Single(ledger.CommandsOfType<PickCommand>());
        Assert.Single(ledger.CommandsOfType<InvoiceCommand>());

        await Mediator(sp).Publish(new Dispatched(id), default);
        Assert.Single(ledger.CommandsOfType<DispatchCommand>());
        Assert.Null(await Manager(sp).GetAsync(id, default));
    }

    [Fact]
    public async Task The_Second_Steps_Event_Does_Not_Start_The_Saga()
    {
        // One shared trigger would let Billing.Placed fire the first step's transition.
        var (sp, ledger) = BuildHost();
        var id = new OrderId(2);

        await Mediator(sp).Publish(new Fixtures.Billing.Placed(id), default);

        Assert.Empty(ledger.AllCommands);
        // Auto-created by LoadOrCreate, as for any late event, but its FSM rejected the trigger.
        var saga = await Manager(sp).GetAsync(id, default);
        Assert.NotNull(saga);
        Assert.Equal(SameNamedEventsSagaFsm.State.NotStarted, saga!.Fsm.Current);
    }

    [Fact]
    public async Task The_First_Steps_Event_Does_Not_Advance_A_Saga_Past_It()
    {
        var (sp, ledger) = BuildHost();
        var id = new OrderId(3);

        await Mediator(sp).Publish(new Fixtures.Warehouse.Placed(id), default);
        await Mediator(sp).Publish(new Fixtures.Warehouse.Placed(id), default);

        Assert.Single(ledger.CommandsOfType<PickCommand>());
        Assert.Empty(ledger.CommandsOfType<InvoiceCommand>());
    }

    [Fact]
    public async Task Each_Same_Named_Failure_Event_Compensates_Its_Own_Step()
    {
        var (sp, ledger) = BuildHost();
        var id = new OrderId(4);

        await Mediator(sp).Publish(new Fixtures.Warehouse.Placed(id), default);
        // Billing.Rejected compensates step 2, which has not run: it must not compensate step 1.
        await Mediator(sp).Publish(new Fixtures.Billing.Rejected(id), default);
        Assert.Empty(ledger.CommandsOfType<UnpickCommand>());

        await Mediator(sp).Publish(new Fixtures.Warehouse.Rejected(id), default);
        Assert.Single(ledger.CommandsOfType<UnpickCommand>());
        Assert.Empty(ledger.CommandsOfType<VoidInvoiceCommand>());
        Assert.Null(await Manager(sp).GetAsync(id, default));
    }

    [Fact]
    public async Task The_Second_Failure_Event_Compensates_Both_Steps()
    {
        var (sp, ledger) = BuildHost();
        var id = new OrderId(5);

        await Mediator(sp).Publish(new Fixtures.Warehouse.Placed(id), default);
        await Mediator(sp).Publish(new Fixtures.Billing.Placed(id), default);
        await Mediator(sp).Publish(new Fixtures.Billing.Rejected(id), default);

        Assert.Single(ledger.CommandsOfType<VoidInvoiceCommand>());
        Assert.Single(ledger.CommandsOfType<UnpickCommand>());
        Assert.Null(await Manager(sp).GetAsync(id, default));
    }
}
