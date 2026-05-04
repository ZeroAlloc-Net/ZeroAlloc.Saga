using System;
using System.Buffers;
using System.Buffers.Binary;
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

// Step commands are deliberately NOT partial. ZASAGA016 (warning) is suppressed
// at the csproj level — we hand-roll AOT-safe byte ISerializer<T> impls below
// rather than route through ZeroAlloc.Serialisation's source-gen path.
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

    public static CommandCounters? Current { get; set; }
}

internal sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
    { CommandCounters.Current!.Reserve++; return new(Unit.Value); }
}
internal sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(ChargeCustomerCommand req, CancellationToken ct)
    { CommandCounters.Current!.Charge++; return new(Unit.Value); }
}
internal sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
    { CommandCounters.Current!.Ship++; return new(Unit.Value); }
}
internal sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public ValueTask<Unit> Handle(CancelReservationCommand req, CancellationToken ct)
    { CommandCounters.Current!.Cancel++; return new(Unit.Value); }
}
internal sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public ValueTask<Unit> Handle(RefundPaymentCommand req, CancellationToken ct)
    { CommandCounters.Current!.Refund++; return new(Unit.Value); }
}

// ── Hand-rolled AOT-safe ISerializer<T> impls ────────────────────────────────
//
// Each command is just a couple of primitives. Encoding: [int OrderId.V, decimal Total?].
// Using BinaryPrimitives + decimal.GetBits avoids reflection entirely and trims
// to a few dozen IL instructions — the trimmer keeps everything that's
// statically reachable without ceremony.

internal static class ByteCodec
{
    public static void WriteOrderIdAndDecimal(IBufferWriter<byte> w, OrderId id, decimal total)
    {
        var span = w.GetSpan(20);
        BinaryPrimitives.WriteInt32LittleEndian(span, id.V);
        Span<int> bits = stackalloc int[4];
        var written = decimal.GetBits(total, bits);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], bits[0]);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], bits[1]);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], bits[2]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], bits[3]);
        _ = written;
        w.Advance(20);
    }

    public static (OrderId Id, decimal Total) ReadOrderIdAndDecimal(ReadOnlySpan<byte> buf)
    {
        var id = new OrderId(BinaryPrimitives.ReadInt32LittleEndian(buf));
        Span<int> bits = stackalloc int[4]
        {
            BinaryPrimitives.ReadInt32LittleEndian(buf[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(buf[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(buf[12..]),
            BinaryPrimitives.ReadInt32LittleEndian(buf[16..]),
        };
        return (id, new decimal(bits));
    }

    public static void WriteOrderId(IBufferWriter<byte> w, OrderId id)
    {
        var span = w.GetSpan(4);
        BinaryPrimitives.WriteInt32LittleEndian(span, id.V);
        w.Advance(4);
    }

    public static OrderId ReadOrderId(ReadOnlySpan<byte> buf)
        => new(BinaryPrimitives.ReadInt32LittleEndian(buf));
}

internal sealed class ReserveStockSerializer : ISerializer<ReserveStockCommand>
{
    public void Serialize(IBufferWriter<byte> w, ReserveStockCommand v) => ByteCodec.WriteOrderIdAndDecimal(w, v.OrderId, v.Total);
    public ReserveStockCommand Deserialize(ReadOnlySpan<byte> buf)
    { var (id, total) = ByteCodec.ReadOrderIdAndDecimal(buf); return new(id, total); }
}

internal sealed class ChargeCustomerSerializer : ISerializer<ChargeCustomerCommand>
{
    public void Serialize(IBufferWriter<byte> w, ChargeCustomerCommand v) => ByteCodec.WriteOrderIdAndDecimal(w, v.OrderId, v.Total);
    public ChargeCustomerCommand Deserialize(ReadOnlySpan<byte> buf)
    { var (id, total) = ByteCodec.ReadOrderIdAndDecimal(buf); return new(id, total); }
}

internal sealed class ShipOrderSerializer : ISerializer<ShipOrderCommand>
{
    public void Serialize(IBufferWriter<byte> w, ShipOrderCommand v) => ByteCodec.WriteOrderId(w, v.OrderId);
    public ShipOrderCommand Deserialize(ReadOnlySpan<byte> buf) => new(ByteCodec.ReadOrderId(buf));
}

internal sealed class CancelReservationSerializer : ISerializer<CancelReservationCommand>
{
    public void Serialize(IBufferWriter<byte> w, CancelReservationCommand v) => ByteCodec.WriteOrderId(w, v.OrderId);
    public CancelReservationCommand Deserialize(ReadOnlySpan<byte> buf) => new(ByteCodec.ReadOrderId(buf));
}

internal sealed class RefundPaymentSerializer : ISerializer<RefundPaymentCommand>
{
    public void Serialize(IBufferWriter<byte> w, RefundPaymentCommand v) => ByteCodec.WriteOrderId(w, v.OrderId);
    public RefundPaymentCommand Deserialize(ReadOnlySpan<byte> buf) => new(ByteCodec.ReadOrderId(buf));
}
