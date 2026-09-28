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

namespace ZeroAlloc.Saga.MultiAssembly.Billing;

public readonly record struct InvoiceId(int V);

public sealed record InvoiceRequested(InvoiceId Id) : INotification;

public sealed record InvoicePaid(InvoiceId Id) : INotification;

// Declared partial with a user-applied [ZeroAllocSerializable], as in the Outbox test fixture, so
// the Saga generator leaves the attribute alone and the registry can deserialise it.
[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record IssueInvoiceCommand(InvoiceId Id) : IRequest;

[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record CloseInvoiceCommand(InvoiceId Id) : IRequest;

[System.Text.Json.Serialization.JsonSerializable(typeof(IssueInvoiceCommand))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CloseInvoiceCommand))]
public sealed partial class BillingJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

[Saga]
public partial class InvoiceSaga
{
    public InvoiceId Id { get; set; }

    [CorrelationKey] public InvoiceId Correlation(InvoiceRequested e) => e.Id;
    [CorrelationKey] public InvoiceId Correlation(InvoicePaid e) => e.Id;

    [Step(Order = 1)]
    public IssueInvoiceCommand Handle(InvoiceRequested e)
    {
        Id = e.Id;
        return new IssueInvoiceCommand(e.Id);
    }

    // Two steps, so the saga takes both paths that commit a step's outbox row: the first step saves
    // the saga, and the last removes it.
    [Step(Order = 2)]
    public CloseInvoiceCommand Close(InvoicePaid e) => new(e.Id);
}

/// <summary>
/// Records every command this assembly's mediator delivered. Ambient per test:
/// a test sets <see cref="Current"/> before starting the host, and the outbox worker's execution
/// context flows from there.
/// </summary>
public sealed class BillingLedger
{
    private static readonly AsyncLocal<BillingLedger?> s_current = new();

    public static BillingLedger? Current { get => s_current.Value; set => s_current.Value = value; }

    private readonly Lock _gate = new();
    private readonly List<object> _commands = new();

    public IReadOnlyList<T> CommandsOfType<T>() { lock (_gate) return _commands.OfType<T>().ToArray(); }

    internal void Record(object cmd) { lock (_gate) _commands.Add(cmd); }
}

public sealed class IssueInvoiceHandler : IRequestHandler<IssueInvoiceCommand, Unit>
{
    public ValueTask<Unit> Handle(IssueInvoiceCommand request, CancellationToken cancellationToken)
    {
        BillingLedger.Current?.Record(request);
        return new ValueTask<Unit>(Unit.Value);
    }
}

public sealed class CloseInvoiceHandler : IRequestHandler<CloseInvoiceCommand, Unit>
{
    public ValueTask<Unit> Handle(CloseInvoiceCommand request, CancellationToken cancellationToken)
    {
        BillingLedger.Current?.Record(request);
        return new ValueTask<Unit>(Unit.Value);
    }
}

/// <summary>
/// What a test needs from this assembly. The generated IMediator is internal to each compilation,
/// so the test cannot publish through it directly; <see cref="PublishAsync"/> does it here.
/// </summary>
public static class BillingFixture
{
    public static IServiceCollection AddBillingFixture(this IServiceCollection services)
    {
        services.TryAddTransient<IssueInvoiceHandler>();
        services.TryAddTransient<CloseInvoiceHandler>();
        services.TryAddSingleton<ISerializer<IssueInvoiceCommand>, JsonCommandSerializer<IssueInvoiceCommand>>();
        services.TryAddSingleton<ISerializer<CloseInvoiceCommand>, JsonCommandSerializer<CloseInvoiceCommand>>();
        return services;
    }

    public static async Task PublishAsync(IServiceProvider services, InvoiceRequested evt)
    {
        var scope = services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
        }
    }

    public static async Task PublishAsync(IServiceProvider services, InvoicePaid evt)
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
