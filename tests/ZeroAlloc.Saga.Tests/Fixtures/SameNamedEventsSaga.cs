using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

// Two events named Placed and two named Rejected, in different namespaces, drive one saga. The
// generator gives each its own handler class and FSM trigger, #216.
namespace ZeroAlloc.Saga.Tests.Fixtures.Warehouse
{
    public sealed record Placed(OrderId OrderId) : INotification;
    public sealed record Rejected(OrderId OrderId) : INotification;
}

namespace ZeroAlloc.Saga.Tests.Fixtures.Billing
{
    public sealed record Placed(OrderId OrderId) : INotification;
    public sealed record Rejected(OrderId OrderId) : INotification;
}

namespace ZeroAlloc.Saga.Tests.Fixtures
{
    public sealed record Dispatched(OrderId OrderId) : INotification;

    public sealed record PickCommand(OrderId OrderId) : IRequest;
    public sealed record UnpickCommand(OrderId OrderId) : IRequest;
    public sealed record InvoiceCommand(OrderId OrderId) : IRequest;
    public sealed record VoidInvoiceCommand(OrderId OrderId) : IRequest;
    public sealed record DispatchCommand(OrderId OrderId) : IRequest;

    [Saga]
    public partial class SameNamedEventsSaga
    {
        public OrderId OrderId { get; private set; }

        [CorrelationKey] public OrderId Correlation(Warehouse.Placed e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(Billing.Placed e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(Warehouse.Rejected e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(Billing.Rejected e) => e.OrderId;
        [CorrelationKey] public OrderId Correlation(Dispatched e) => e.OrderId;

        [Step(Order = 1, Compensate = nameof(Unpick), CompensateOn = typeof(Warehouse.Rejected))]
        public PickCommand Pick(Warehouse.Placed e)
        {
            OrderId = e.OrderId;
            return new PickCommand(e.OrderId);
        }

        [Step(Order = 2, Compensate = nameof(VoidInvoice), CompensateOn = typeof(Billing.Rejected))]
        public InvoiceCommand Invoice(Billing.Placed e) => new(OrderId);

        [Step(Order = 3)]
        public DispatchCommand Dispatch(Dispatched e) => new(OrderId);

        public UnpickCommand Unpick() => new(OrderId);
        public VoidInvoiceCommand VoidInvoice() => new(OrderId);
    }

    public sealed class PickHandler : IRequestHandler<PickCommand, Unit>
    {
        public async ValueTask<Unit> Handle(PickCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class UnpickHandler : IRequestHandler<UnpickCommand, Unit>
    {
        public async ValueTask<Unit> Handle(UnpickCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class InvoiceHandler : IRequestHandler<InvoiceCommand, Unit>
    {
        public async ValueTask<Unit> Handle(InvoiceCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class VoidInvoiceHandler : IRequestHandler<VoidInvoiceCommand, Unit>
    {
        public async ValueTask<Unit> Handle(VoidInvoiceCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }

    public sealed class DispatchHandler : IRequestHandler<DispatchCommand, Unit>
    {
        public async ValueTask<Unit> Handle(DispatchCommand req, CancellationToken ct)
        { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
    }
}
