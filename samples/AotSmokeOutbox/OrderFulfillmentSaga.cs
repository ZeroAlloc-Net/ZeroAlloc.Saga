using System;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;
using ZeroAlloc.Serialisation;

// ── Minimal saga shape — kept self-contained to make AOT review trivial ──

namespace AotSmokeOutbox;

public readonly record struct OrderId(int V) : IEquatable<OrderId>;

public sealed record OrderPlaced(OrderId OrderId, decimal Total) : INotification;
public sealed record StockReserved(OrderId OrderId) : INotification;
public sealed record PaymentCharged(OrderId OrderId) : INotification;
public sealed record PaymentDeclined(OrderId OrderId) : INotification;
public sealed record OrderShipped(OrderId OrderId) : INotification;

// Every command the outbox carries needs an ISerializer<T>. Here the user applies
// [ZeroAllocSerializable] and lists the type on a JsonSerializerContext, so
// ZeroAlloc.Serialisation's generator emits an AOT-safe serializer and an
// Add{Type}Serializer() DI extension for each. Registering a hand-written ISerializer<T>
// works as well. The Saga generator does not add the attribute, #207.
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record ReserveStockCommand(OrderId OrderId, decimal Total) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record ChargeCustomerCommand(OrderId OrderId, decimal Total) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record ShipOrderCommand(OrderId OrderId) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record CancelReservationCommand(OrderId OrderId) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed record RefundPaymentCommand(OrderId OrderId) : IRequest;

// A struct step command, as Mediator's ZAM003 recommends. The generated SagaCommandRegistry
// must not null-check it, since comparing a struct to null does not compile, #202.
[StructLayout(LayoutKind.Auto)]
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public readonly record struct NotifyCustomerCommand(OrderId OrderId, int Sequence) : IRequest;

// The System.Text.Json source-generated metadata the generated serializers use. Without a
// [JsonSerializable] entry here, ZeroAlloc.Serialisation reports ZASZ004 and emits nothing.
[JsonSerializable(typeof(ReserveStockCommand))]
[JsonSerializable(typeof(ChargeCustomerCommand))]
[JsonSerializable(typeof(ShipOrderCommand))]
[JsonSerializable(typeof(CancelReservationCommand))]
[JsonSerializable(typeof(RefundPaymentCommand))]
[JsonSerializable(typeof(NotifyCustomerCommand))]
internal sealed partial class CommandJsonContext : JsonSerializerContext;

[Saga]
public partial class OrderFulfillmentSaga
{
    public OrderId OrderId { get; set; }
    public decimal Total { get; set; }

    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentCharged e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(OrderShipped e) => e.OrderId;

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

    [Step(Order = 4)]
    public NotifyCustomerCommand NotifyCustomer(OrderShipped e) => new(OrderId, 7);

    public CancelReservationCommand CancelReservation() => new(OrderId);
    public RefundPaymentCommand RefundPayment() => new(OrderId);
}

/// <summary>
/// Per-command counters surfaced via an ambient slot so each handler can record
/// without ctor injection (the Mediator dispatcher's no-factory fallback path
/// requires a parameterless ctor, so we avoid DI for handlers).
/// </summary>
internal sealed class CommandCounters
{
    public int Reserve;
    public int Charge;
    public int Ship;
    public int Cancel;
    public int Refund;
    public int Notify;
    public NotifyCustomerCommand LastNotify;

    public static CommandCounters? Current { get; set; }
}

internal sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
    { Interlocked.Increment(ref CommandCounters.Current!.Reserve); return new(Unit.Value); }
}
internal sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(ChargeCustomerCommand req, CancellationToken ct)
    { Interlocked.Increment(ref CommandCounters.Current!.Charge); return new(Unit.Value); }
}
internal sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
    { Interlocked.Increment(ref CommandCounters.Current!.Ship); return new(Unit.Value); }
}
internal sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public ValueTask<Unit> Handle(CancelReservationCommand req, CancellationToken ct)
    { Interlocked.Increment(ref CommandCounters.Current!.Cancel); return new(Unit.Value); }
}
internal sealed class NotifyCustomerHandler : IRequestHandler<NotifyCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(NotifyCustomerCommand req, CancellationToken ct)
    {
        CommandCounters.Current!.LastNotify = req;
        Interlocked.Increment(ref CommandCounters.Current.Notify);
        return new(Unit.Value);
    }
}
internal sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public ValueTask<Unit> Handle(RefundPaymentCommand req, CancellationToken ct)
    { Interlocked.Increment(ref CommandCounters.Current!.Refund); return new(Unit.Value); }
}
