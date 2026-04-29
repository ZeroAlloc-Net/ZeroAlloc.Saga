using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Tests.Fixtures;

// Second command for the multi-saga test.
public sealed record AuditOrderCommand(OrderId OrderId) : IRequest;

[Saga]
public partial class RefundSaga
{
    public OrderId OrderId { get; private set; }

    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;

    [Step(Order = 1)]
    public AuditOrderCommand Audit(OrderPlaced e)
    {
        OrderId = e.OrderId;
        return new AuditOrderCommand(e.OrderId);
    }
}
