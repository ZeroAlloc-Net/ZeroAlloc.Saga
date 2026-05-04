using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox.Tests.Fixtures;

public readonly record struct OrderId(int V) : IEquatable<OrderId>;

// Notification events (saga inputs).
public sealed record OrderPlaced(OrderId OrderId, decimal Total) : INotification;
public sealed record StockReserved(OrderId OrderId) : INotification;
public sealed record PaymentCharged(OrderId OrderId) : INotification;
public sealed record PaymentDeclined(OrderId OrderId) : INotification;

// Step commands (saga outputs). Each is declared partial AND user-applies
// [ZeroAllocSerializable(SystemTextJson)] so the saga generator's
// SerializableExtensionEmitter skips its (broken-against-2.1.0) auto-emission.
// Hand-rolled JSON serializers are registered separately in AddTestSerializers
// — they win over any generator-emitted ISerializer<T> via DI registration order.
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record ReserveStockCommand(OrderId OrderId, decimal Total) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record ChargeCustomerCommand(OrderId OrderId, decimal Total) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record ShipOrderCommand(OrderId OrderId) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record CancelReservationCommand(OrderId OrderId) : IRequest;
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record RefundPaymentCommand(OrderId OrderId) : IRequest;

// Required for SystemTextJson AOT-safe source-generated path: a
// JsonSerializerContext-derived class lists every [JsonSerializable] type
// the ZeroAlloc.Serialisation generator's ZASZ004 check expects. Without
// this, ZASZ004 fires (an error). The hand-rolled JsonCommandSerializer<T>
// below uses reflection-based JsonSerializer; the source-gen path is
// satisfied as a compile-time prerequisite only.
[System.Text.Json.Serialization.JsonSerializable(typeof(ReserveStockCommand))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ChargeCustomerCommand))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ShipOrderCommand))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CancelReservationCommand))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RefundPaymentCommand))]
public sealed partial class CommandJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

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

/// <summary>
/// Trivial reflection-free <see cref="ISerializer{T}"/> built on
/// <see cref="JsonSerializer"/>. Registered manually for each step command so
/// <see cref="OutboxSagaCommandDispatcher"/>'s
/// <c>services.GetRequiredService&lt;ISerializer&lt;TCmd&gt;&gt;()</c> resolves
/// without requiring the auto-emitted <c>[ZeroAllocSerializable]</c> path
/// (whose <c>SerializationFormat.Json</c> enum value was renamed before
/// Serialisation 2.1.0 and would not compile here).
/// </summary>
public sealed class JsonCommandSerializer<T> : ISerializer<T>
{
    public void Serialize(IBufferWriter<byte> writer, T value)
    {
        using var w = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(w, value);
    }

    public T Deserialize(ReadOnlySpan<byte> buffer)
        => JsonSerializer.Deserialize<T>(buffer)!;
}

/// <summary>
/// One-stop registration helper that wires per-command <see cref="JsonCommandSerializer{T}"/>
/// instances into the test host. Keeps the test bodies small and ensures a
/// consistent ISerializer surface for the OutboxSagaCommandDispatcher and the
/// generator-emitted SagaCommandRegistry's deserialize-and-dispatch path.
/// </summary>
public static class TestSerializerRegistration
{
    public static IServiceCollection AddTestSerializers(this IServiceCollection services)
    {
        services.AddSingleton<ISerializer<ReserveStockCommand>, JsonCommandSerializer<ReserveStockCommand>>();
        services.AddSingleton<ISerializer<ChargeCustomerCommand>, JsonCommandSerializer<ChargeCustomerCommand>>();
        services.AddSingleton<ISerializer<ShipOrderCommand>, JsonCommandSerializer<ShipOrderCommand>>();
        services.AddSingleton<ISerializer<CancelReservationCommand>, JsonCommandSerializer<CancelReservationCommand>>();
        services.AddSingleton<ISerializer<RefundPaymentCommand>, JsonCommandSerializer<RefundPaymentCommand>>();
        return services;
    }
}
