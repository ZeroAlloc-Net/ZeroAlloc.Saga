using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox.Tests.Fixtures;

public readonly record struct CustomerId(int V) : IEquatable<CustomerId>;

public sealed record CustomerRegistered(CustomerId CustomerId) : INotification;

[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]
public sealed partial record SendWelcomeCommand(CustomerId CustomerId) : IRequest;

[System.Text.Json.Serialization.JsonSerializable(typeof(SendWelcomeCommand))]
public sealed partial class WelcomeJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

/// <summary>
/// A single-step saga: the one event that starts it also completes it, so the generated handler
/// dispatches the command and then removes a saga that was never saved, #194.
/// </summary>
[Saga]
public partial class WelcomeSaga
{
    public CustomerId CustomerId { get; set; }

    [CorrelationKey] public CustomerId Correlation(CustomerRegistered e) => e.CustomerId;

    [Step(Order = 1)]
    public SendWelcomeCommand SendWelcome(CustomerRegistered e)
    {
        CustomerId = e.CustomerId;
        return new SendWelcomeCommand(e.CustomerId);
    }
}

public sealed class SendWelcomeHandler : IRequestHandler<SendWelcomeCommand, Unit>
{
    public async ValueTask<Unit> Handle(SendWelcomeCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public static class WelcomeSagaFixture
{
    public static IServiceCollection AddWelcomeSagaFixture(this IServiceCollection services)
    {
        services.TryAddTransient<SendWelcomeHandler>();
        services.TryAddSingleton<ISerializer<SendWelcomeCommand>, JsonCommandSerializer<SendWelcomeCommand>>();
        return services;
    }
}
