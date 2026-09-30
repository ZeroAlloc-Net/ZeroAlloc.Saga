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
/// Events named like the FSM's built-in triggers, Complete and CompensateDone. They must drive
/// only their own steps and compensations, never complete a saga or end a compensation, #219.
/// </summary>
public class ReservedTriggerNameTests
{
    private static (IServiceProvider Sp, CommandLedger Ledger) BuildHost()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddMediator();
        services.TryAddTransient<OpenCheckoutHandler>();
        services.TryAddTransient<ChargeCheckoutHandler>();
        services.TryAddTransient<ShipCheckoutHandler>();
        services.TryAddTransient<StartReturnHandler>();
        services.TryAddTransient<FinishReturnHandler>();
        services.TryAddTransient<UndoStartReturnHandler>();
        services.TryAddTransient<UndoFinishReturnHandler>();
        services.AddSaga().WithCompleteNamedStepEventSaga().WithCompleteNamedCompensateOnSaga();

        var ledger = new CommandLedger(null);
        CommandLedger.Current = ledger;
        return (services.BuildServiceProvider(), ledger);
    }

    private static IMediator Mediator(IServiceProvider sp) => sp.GetRequiredService<IMediator>();

    [Fact]
    public async Task A_Repeated_Step1_Event_Named_Complete_At_Step2_Neither_Completes_Nor_Redispatches()
    {
        var (sp, ledger) = BuildHost();
        var manager = sp.GetRequiredService<ISagaManager<CompleteNamedStepEventSaga, OrderId>>();
        var id = new OrderId(1);

        await Mediator(sp).Publish(new Fixtures.Checkout.Complete(id), default);
        await Mediator(sp).Publish(new CheckoutPaid(id), default);
        await Mediator(sp).Publish(new Fixtures.Checkout.Complete(id), default);

        Assert.Single(ledger.CommandsOfType<OpenCheckoutCommand>());
        var saga = await manager.GetAsync(id, default);
        Assert.NotNull(saga);
        Assert.Equal(CompleteNamedStepEventSagaFsm.State.Step2, saga!.Fsm.Current);

        // The saga still runs its last step and completes.
        await Mediator(sp).Publish(new CheckoutShipped(id), default);
        Assert.Single(ledger.CommandsOfType<ShipCheckoutCommand>());
        Assert.Null(await manager.GetAsync(id, default));
    }

    [Fact]
    public async Task A_CompensateOn_Event_Named_Complete_Compensates_The_Steps_That_Ran()
    {
        var (sp, ledger) = BuildHost();
        var manager = sp.GetRequiredService<ISagaManager<CompleteNamedCompensateOnSaga, OrderId>>();
        var id = new OrderId(2);

        await Mediator(sp).Publish(new ReturnStarted(id), default);
        await Mediator(sp).Publish(new Fixtures.Returns.Complete(id), default);

        Assert.Single(ledger.CommandsOfType<UndoStartReturnCommand>());
        Assert.Empty(ledger.CommandsOfType<UndoFinishReturnCommand>());
        Assert.Null(await manager.GetAsync(id, default));
    }

    [Fact]
    public async Task A_Step_Event_Named_CompensateDone_Drives_Its_Step_And_Completes_The_Saga()
    {
        var (sp, ledger) = BuildHost();
        var manager = sp.GetRequiredService<ISagaManager<CompleteNamedCompensateOnSaga, OrderId>>();
        var id = new OrderId(3);

        await Mediator(sp).Publish(new ReturnStarted(id), default);
        await Mediator(sp).Publish(new Fixtures.Returns.CompensateDone(id), default);

        Assert.Single(ledger.CommandsOfType<StartReturnCommand>());
        Assert.Single(ledger.CommandsOfType<FinishReturnCommand>());
        Assert.Empty(ledger.CommandsOfType<UndoStartReturnCommand>());
        Assert.Null(await manager.GetAsync(id, default));
    }
}
