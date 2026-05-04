using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga;

/// <summary>
/// Default <see cref="ISagaCommandDispatcher"/>: forwards to the per-consumer-assembly
/// <c>IMediator.Send</c> implementation emitted by <c>ZeroAlloc.Mediator.Generator</c>.
/// Registered automatically by <c>services.AddSaga&lt;TSaga&gt;()</c>. Replace with
/// <c>WithOutbox()</c> from <c>ZeroAlloc.Saga.Outbox</c> for transactional dispatch.
/// </summary>
/// <remarks>
/// The mediator parameter is typed as <see cref="object"/> rather than
/// <c>ZeroAlloc.Mediator.IMediator</c> because <c>IMediator</c> is a per-assembly
/// partial interface emitted by Mediator's source generator into each consumer
/// assembly — it does not exist as a compiled type in <c>ZeroAlloc.Mediator</c>.
/// Dispatch resolves the matching <c>Send(TCommand, CancellationToken)</c> overload
/// via reflection (cached per command type) so the call works regardless of which
/// assembly emitted the consumer's <c>IMediator</c>.
/// </remarks>
public sealed class MediatorSagaCommandDispatcher : ISagaCommandDispatcher
{
    // Per-command-type cache of the matching IMediator.Send(TCommand, CancellationToken)
    // MethodInfo. Keyed by TCommand because every Mediator-generated overload is
    // distinguished only by its first parameter type.
    private static class SendCache<TCommand>
    {
        public static MethodInfo? Method;
    }

    private readonly object _mediator;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
    private readonly Type _mediatorType;

    /// <summary>
    /// Creates a dispatcher that forwards to the supplied mediator. Pass the
    /// <c>IMediator</c> resolved from the consumer's DI container.
    /// </summary>
    /// <param name="mediator">
    /// The consumer assembly's <c>ZeroAlloc.Mediator.IMediator</c> instance. Must
    /// expose a <c>Send(TCommand, CancellationToken)</c> overload for every command
    /// Saga will dispatch (the Mediator generator emits one per
    /// <see cref="IRequest{TResponse}"/>).
    /// </param>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2074:value stored in field does not satisfy 'DynamicallyAccessedMembers' requirements",
        Justification = "Send is generated as a public instance method on MediatorService by " +
                        "ZeroAlloc.Mediator.Generator; the consumer must reference the generator " +
                        "(per docs) for AOT-compatible dispatch, which preserves those methods.")]
    public MediatorSagaCommandDispatcher(object mediator)
    {
        _mediator = mediator;
        // Capture the concrete runtime type so we can resolve Send overloads against
        // the Mediator-generator-emitted partial. PublicMethods is sufficient — Send
        // is always emitted as a public instance method on MediatorService.
        _mediatorType = mediator.GetType();
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:'this' argument does not satisfy 'DynamicallyAccessedMembersAttribute'",
        Justification = "_mediatorType is annotated with PublicMethods; the Send overloads " +
                        "are always public instance members on the consumer's MediatorService.")]
    public async ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        var send = SendCache<TCommand>.Method;
        if (send is null)
        {
            send = _mediatorType.GetMethod(
                "Send",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(TCommand), typeof(CancellationToken) },
                modifiers: null)
                ?? throw new InvalidOperationException(
                    $"IMediator does not expose a Send({typeof(TCommand).FullName}, CancellationToken) " +
                    "overload. Confirm the command type is reachable from the Mediator generator " +
                    "(it must implement IRequest<Unit> in an assembly that references " +
                    "ZeroAlloc.Mediator.Generator).");
            SendCache<TCommand>.Method = send;
        }

        var result = send.Invoke(_mediator, new object?[] { cmd, ct });
        // Send returns ValueTask<Unit>; await it to surface handler exceptions.
        if (result is ValueTask<Unit> vt)
        {
            await vt.ConfigureAwait(false);
        }
        else if (result is Task<Unit> t)
        {
            await t.ConfigureAwait(false);
        }
    }
}
