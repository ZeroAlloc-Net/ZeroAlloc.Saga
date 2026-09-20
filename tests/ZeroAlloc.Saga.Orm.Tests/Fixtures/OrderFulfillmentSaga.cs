using System;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Orm.Tests.Fixtures;

public readonly record struct OrderId(int V) : IEquatable<OrderId>;

// Notification events (saga inputs).
public sealed record OrderPlaced(OrderId OrderId, decimal Total) : INotification;
public sealed record StockReserved(OrderId OrderId) : INotification;
public sealed record PaymentCharged(OrderId OrderId) : INotification;
public sealed record PaymentDeclined(OrderId OrderId) : INotification;

// Commands (saga outputs).
public sealed partial record ReserveStockCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed partial record ChargeCustomerCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed partial record ShipOrderCommand(OrderId OrderId) : IRequest;
public sealed record CancelReservationCommand(OrderId OrderId) : IRequest;
public sealed record RefundPaymentCommand(OrderId OrderId) : IRequest;

/// <summary>
/// Mirrors the EF Core backend's fixture saga so the two stores are exercised
/// against the same shape.
/// </summary>
[Saga]
public partial class OrderFulfillmentSaga
{
    public OrderId OrderId { get; set; }
    public decimal Total { get; set; }

    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentCharged e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;

    [Step(Order = 1, Compensate = nameof(CancelReservation))]
    public ReserveStockCommand ReserveStock(OrderPlaced e)
    {
        OrderId = e.OrderId;
        Total = e.Total;
        return new ReserveStockCommand(e.OrderId, e.Total);
    }

    [Step(Order = 2, Compensate = nameof(RefundPayment), CompensateOn = typeof(PaymentDeclined))]
    public ChargeCustomerCommand ChargeCustomer(StockReserved e) => new(OrderId, Total);

    [Step(Order = 3)]
    public ShipOrderCommand ShipOrder(PaymentCharged e) => new(OrderId);

    public CancelReservationCommand CancelReservation() => new(OrderId);
    public RefundPaymentCommand RefundPayment() => new(OrderId);
}

// The Mediator generator requires a registered handler per IRequest, so each
// saga command needs one even though these tests assert on persisted rows
// rather than on dispatched commands.
public sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public ValueTask<Unit> Handle(ReserveStockCommand req, System.Threading.CancellationToken ct)
        => ValueTask.FromResult(Unit.Value);
}

public sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(ChargeCustomerCommand req, System.Threading.CancellationToken ct)
        => ValueTask.FromResult(Unit.Value);
}

public sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public ValueTask<Unit> Handle(ShipOrderCommand req, System.Threading.CancellationToken ct)
        => ValueTask.FromResult(Unit.Value);
}

public sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public ValueTask<Unit> Handle(CancelReservationCommand req, System.Threading.CancellationToken ct)
        => ValueTask.FromResult(Unit.Value);
}

public sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public ValueTask<Unit> Handle(RefundPaymentCommand req, System.Threading.CancellationToken ct)
        => ValueTask.FromResult(Unit.Value);
}
