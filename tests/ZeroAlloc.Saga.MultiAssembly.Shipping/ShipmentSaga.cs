using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.MultiAssembly.Shipping;

public readonly record struct ShipmentId(int V);

public sealed record ShipmentRequested(ShipmentId Id) : INotification;

public sealed record ShipmentDelivered(ShipmentId Id) : INotification;

// Declared partial with a user-applied [ZeroAllocSerializable], as in the Outbox test fixture, so
// the Saga generator leaves the attribute alone and the registry can deserialise it.
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record BookCarrierCommand(ShipmentId Id) : IRequest;

[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record CloseShipmentCommand(ShipmentId Id) : IRequest;

[System.Text.Json.Serialization.JsonSerializable(typeof(BookCarrierCommand))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CloseShipmentCommand))]
public sealed partial class ShippingJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

[Saga]
public partial class ShipmentSaga
{
    public ShipmentId Id { get; set; }

    [CorrelationKey] public ShipmentId Correlation(ShipmentRequested e) => e.Id;
    [CorrelationKey] public ShipmentId Correlation(ShipmentDelivered e) => e.Id;

    [Step(Order = 1)]
    public BookCarrierCommand Handle(ShipmentRequested e)
    {
        Id = e.Id;
        return new BookCarrierCommand(e.Id);
    }

    // A second step keeps the saga open after the first, so its state row is saved and the first
    // step's outbox row commits with it.
    [Step(Order = 2)]
    public CloseShipmentCommand Close(ShipmentDelivered e) => new(e.Id);
}

/// <summary>
/// Records every command this assembly's mediator delivered. Ambient per test:
/// a test sets <see cref="Current"/> before starting the host, and the outbox worker's execution
/// context flows from there.
/// </summary>
public sealed class ShippingLedger
{
    private static readonly AsyncLocal<ShippingLedger?> s_current = new();

    public static ShippingLedger? Current { get => s_current.Value; set => s_current.Value = value; }

    private readonly Lock _gate = new();
    private readonly List<object> _commands = new();

    public IReadOnlyList<T> CommandsOfType<T>() { lock (_gate) return _commands.OfType<T>().ToArray(); }

    internal void Record(object cmd) { lock (_gate) _commands.Add(cmd); }
}

public sealed class BookCarrierHandler : IRequestHandler<BookCarrierCommand, Unit>
{
    public ValueTask<Unit> Handle(BookCarrierCommand request, CancellationToken cancellationToken)
    {
        ShippingLedger.Current?.Record(request);
        return new ValueTask<Unit>(Unit.Value);
    }
}

public sealed class CloseShipmentHandler : IRequestHandler<CloseShipmentCommand, Unit>
{
    public ValueTask<Unit> Handle(CloseShipmentCommand request, CancellationToken cancellationToken)
    {
        ShippingLedger.Current?.Record(request);
        return new ValueTask<Unit>(Unit.Value);
    }
}

/// <summary>
/// What a test needs from this assembly. The generated IMediator is internal to each compilation,
/// so the test cannot publish through it directly; <see cref="PublishAsync"/> does it here.
/// </summary>
public static class ShippingFixture
{
    public static IServiceCollection AddShippingFixture(this IServiceCollection services)
    {
        services.TryAddTransient<BookCarrierHandler>();
        services.TryAddTransient<CloseShipmentHandler>();
        services.TryAddSingleton<ISerializer<BookCarrierCommand>, JsonCommandSerializer<BookCarrierCommand>>();
        services.TryAddSingleton<ISerializer<CloseShipmentCommand>, JsonCommandSerializer<CloseShipmentCommand>>();
        return services;
    }

    public static async Task PublishAsync(IServiceProvider services, ShipmentRequested evt)
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
        }
    }
}

internal sealed class JsonCommandSerializer<T> : ISerializer<T>
{
    public void Serialize(IBufferWriter<byte> writer, T value)
    {
        using var w = new Utf8JsonWriter(writer);
        JsonSerializer.Serialize(w, value);
    }

    public T Deserialize(ReadOnlySpan<byte> buffer) => JsonSerializer.Deserialize<T>(buffer)!;
}
