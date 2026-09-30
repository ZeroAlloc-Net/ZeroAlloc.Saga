using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

// Events whose simple names are the FSM's built-in triggers, Complete and CompensateDone. The
// generator gives each its own trigger, so it cannot complete a saga or finish a compensation,
// #219.
namespace ZeroAlloc.Saga.Tests.Fixtures.Checkout
{
    public sealed record Complete(OrderId OrderId) : INotification;
}

namespace ZeroAlloc.Saga.Tests.Fixtures.Returns
{
    public sealed record Complete(OrderId OrderId) : INotification;
    public sealed record CompensateDone(OrderId OrderId) : INotification;
}

namespace ZeroAlloc.Saga.Tests.Fixtures
{
    public sealed record CheckoutPaid(OrderId OrderId) : INotification;
    public sealed record CheckoutShipped(OrderId OrderId) : INotification;
    public sealed record ReturnStarted(OrderId OrderId) : INotification;
    public sealed record ReturnFinished(OrderId OrderId) : INotification;

    public sealed record OpenCheckoutCommand(OrderId OrderId) : IRequest;
    public sealed record ChargeCheckoutCommand(OrderId OrderId) : IRequest;
    public sealed record ShipCheckoutCommand(OrderId OrderId) : IRequest;
    public sealed record StartReturnCommand(OrderId OrderId) : IRequest;
    public sealed record FinishReturnCommand(OrderId OrderId) : IRequest;
    public sealed record UndoStartReturnCommand(OrderId OrderId) : IRequest;
    public sealed record UndoFinishReturnCommand(OrderId OrderId) : IRequest;

    // Step 1's event is named Complete, like the trigger that completes the saga.
    [Saga]
    public partial class CompleteNamedStepEventSaga
    {
        public OrderId OrderId { get; private set; }

        [CorrelationKey] public OrderId Correlation(Checkout.Complete e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(CheckoutPaid e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(CheckoutShipped e) => e.OrderId;

        [Step(Order = 1)]
        public OpenCheckoutCommand Open(Checkout.Complete e)
        {
            OrderId = e.OrderId;
            return new OpenCheckoutCommand(e.OrderId);
        }

        [Step(Order = 2)]
        public ChargeCheckoutCommand Charge(CheckoutPaid e) => new(OrderId);

        [Step(Order = 3)]
        public ShipCheckoutCommand Ship(CheckoutShipped e) => new(OrderId);
    }

    // Both steps compensate on an event named Complete, including the last step, whose
    // compensation transition used to duplicate the completion transition. CompensateDone
    // names the step-2 event, like the trigger that ends a compensation.
    [Saga]
    public partial class CompleteNamedCompensateOnSaga
    {
        public OrderId OrderId { get; private set; }

        [CorrelationKey] public OrderId Correlation(ReturnStarted e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(Returns.CompensateDone e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(Returns.Complete e) => e.OrderId;

        [Step(Order = 1, Compensate = nameof(UndoStart), CompensateOn = typeof(Returns.Complete))]
        public StartReturnCommand Start(ReturnStarted e)
        {
            OrderId = e.OrderId;
            return new StartReturnCommand(e.OrderId);
        }

        [Step(Order = 2, Compensate = nameof(UndoFinish), CompensateOn = typeof(Returns.Complete))]
        public FinishReturnCommand Finish(Returns.CompensateDone e) => new(OrderId);

        public UndoStartReturnCommand UndoStart() => new(OrderId);
        public UndoFinishReturnCommand UndoFinish() => new(OrderId);
    }

    public sealed class OpenCheckoutHandler : IRequestHandler<OpenCheckoutCommand, Unit>
    {
        public async ValueTask<Unit> Handle(OpenCheckoutCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class ChargeCheckoutHandler : IRequestHandler<ChargeCheckoutCommand, Unit>
    {
        public async ValueTask<Unit> Handle(ChargeCheckoutCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class ShipCheckoutHandler : IRequestHandler<ShipCheckoutCommand, Unit>
    {
        public async ValueTask<Unit> Handle(ShipCheckoutCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class StartReturnHandler : IRequestHandler<StartReturnCommand, Unit>
    {
        public async ValueTask<Unit> Handle(StartReturnCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class FinishReturnHandler : IRequestHandler<FinishReturnCommand, Unit>
    {
        public async ValueTask<Unit> Handle(FinishReturnCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class UndoStartReturnHandler : IRequestHandler<UndoStartReturnCommand, Unit>
    {
        public async ValueTask<Unit> Handle(UndoStartReturnCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class UndoFinishReturnHandler : IRequestHandler<UndoFinishReturnCommand, Unit>
    {
        public async ValueTask<Unit> Handle(UndoFinishReturnCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }
}
