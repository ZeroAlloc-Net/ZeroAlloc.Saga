using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

// Minimal saga shape — kept self-contained so the sample is easy to read.

namespace ResilienceSample;

public readonly record struct OrderId(int V) : IEquatable<OrderId>;

public sealed record OrderPlaced(OrderId OrderId, decimal Total) : INotification;
public sealed record StockReserved(OrderId OrderId) : INotification;
public sealed record PaymentCharged(OrderId OrderId) : INotification;
public sealed record PaymentDeclined(OrderId OrderId) : INotification;

public sealed record ReserveStockCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed record ChargeCustomerCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed record ShipOrderCommand(OrderId OrderId) : IRequest;
public sealed record CancelReservationCommand(OrderId OrderId) : IRequest;
public sealed record RefundPaymentCommand(OrderId OrderId) : IRequest;

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

/// <summary>Counter shared across mediator handler invocations.</summary>
internal sealed class FlakyHandlerStats
{
    public int ReserveStockAttempts;
    public int ChargeCustomerAttempts;
    public int ShipOrderAttempts;
    public static FlakyHandlerStats? Current { get; set; }
}

/// <summary>
/// Flaky handler — fails twice, succeeds on the third call. Demonstrates the
/// resilience layer's retry policy recovering from transient failures.
/// </summary>
internal sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
    {
        var attempt = Interlocked.Increment(ref FlakyHandlerStats.Current!.ReserveStockAttempts);
        if (attempt < 3)
            throw new InvalidOperationException($"ReserveStock receiver hiccup (attempt {attempt})");
        return new(Unit.Value);
    }
}

internal sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(ChargeCustomerCommand req, CancellationToken ct)
    {
        Interlocked.Increment(ref FlakyHandlerStats.Current!.ChargeCustomerAttempts);
        return new(Unit.Value);
    }
}

internal sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
    {
        Interlocked.Increment(ref FlakyHandlerStats.Current!.ShipOrderAttempts);
        return new(Unit.Value);
    }
}

internal sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public ValueTask<Unit> Handle(CancelReservationCommand req, CancellationToken ct) => new(Unit.Value);
}

internal sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public ValueTask<Unit> Handle(RefundPaymentCommand req, CancellationToken ct) => new(Unit.Value);
}
