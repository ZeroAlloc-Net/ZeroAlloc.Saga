using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.EfCore.Tests.Fixtures;

public readonly record struct OrderId(int V) : IEquatable<OrderId>;

// Notification events (saga inputs).
public sealed record OrderPlaced(OrderId OrderId, decimal Total) : INotification;
public sealed record StockReserved(OrderId OrderId) : INotification;
public sealed record PaymentCharged(OrderId OrderId) : INotification;
public sealed record PaymentDeclined(OrderId OrderId) : INotification;

// Commands (saga outputs).
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

/// <summary>Tiny ambient-ledger thread-safe command recorder.</summary>
public sealed class CommandLedger
{
    private static readonly AsyncLocal<CommandLedger?> _current = new();
    public static CommandLedger? Current { get => _current.Value; set => _current.Value = value; }

    private readonly System.Threading.Lock _gate = new();
    private readonly List<object> _all = new();

    public IReadOnlyList<object> AllCommands { get { lock (_gate) return _all.ToArray(); } }

    public IReadOnlyList<T> CommandsOfType<T>()
    {
        lock (_gate)
        {
            var list = new List<T>();
            foreach (var cmd in _all)
                if (cmd is T t) list.Add(t);
            return list;
        }
    }

    public ValueTask RecordAsync(object cmd)
    {
        lock (_gate) _all.Add(cmd);
        return ValueTask.CompletedTask;
    }
}

// Recording handlers feeding into the ambient ledger.
public sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public async ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public async ValueTask<Unit> Handle(ChargeCustomerCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public async ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public async ValueTask<Unit> Handle(CancelReservationCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public async ValueTask<Unit> Handle(RefundPaymentCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}
