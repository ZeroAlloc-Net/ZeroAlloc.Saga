using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Saga;

/// <summary>
/// The saga commands of one assembly, and how to dispatch them. The Saga generator emits one
/// <c>ZeroAlloc.Saga.Generated.GeneratedSagaCommandSource</c> into every assembly that declares
/// <c>[Saga]</c> classes, and each generator-emitted <c>With{Saga}()</c> adds it to the builder
/// through <see cref="SagaCommandSourceBuilderExtensions.AddCommandSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each assembly needs its own source because the generated <c>IMediator</c> is internal to the
/// compilation that declares the sagas, so only code in that compilation can send their commands.
/// An application that splits its sagas across projects registers one source per project. The
/// default <see cref="ISagaCommandDispatcher"/> routes each command to the source that lists its
/// type, and <c>ZeroAlloc.Saga.Outbox</c>'s <c>WithOutbox()</c> registers an outbox dispatcher for
/// every type of every source.
/// </para>
/// <para>
/// A command type belongs to exactly one source. Adding a second source that lists a type another
/// source already lists throws, naming both assemblies.
/// </para>
/// </remarks>
public abstract class SagaCommandSource
{
    /// <summary>Initializes a new instance of the <see cref="SagaCommandSource"/> class.</summary>
    protected SagaCommandSource()
    {
    }

    /// <summary>
    /// Every step and compensation command type the sagas of this assembly return. The outbox
    /// bridge uses each type's <see cref="Type.FullName"/> as the outbox message type name.
    /// </summary>
    public abstract IReadOnlyList<Type> CommandTypes { get; }

    /// <summary>
    /// True when <see cref="DispatchSerializedAsync"/> can dispatch a serialized command. The
    /// generator implements it when the assembly references <c>ZeroAlloc.Serialisation</c>, which
    /// the outbox bridge needs to write and read saga commands.
    /// </summary>
    public virtual bool CanDispatchSerialized => false;

    /// <summary>
    /// The command types in <see cref="CommandTypes"/> that have no <c>ISerializer&lt;T&gt;</c>
    /// registered in <paramref name="services"/>. <c>ZeroAlloc.Saga.Outbox</c>'s startup check calls
    /// it, so the host fails to start rather than failing at a command's first dispatch. The
    /// generator implements it, with no reflection, when the assembly references
    /// <c>ZeroAlloc.Serialisation</c>.
    /// </summary>
    /// <remarks>
    /// The default returns no types, because a source that does not implement it cannot say which
    /// serializers it needs. A source emitted by an older Saga generator is therefore not checked.
    /// </remarks>
    /// <param name="services">The scoped service provider to resolve the serializers from.</param>
    public virtual IReadOnlyList<Type> GetCommandTypesWithoutSerializer(IServiceProvider services)
        => Array.Empty<Type>();

    /// <summary>
    /// Creates the dispatcher that sends this source's commands through the assembly's own
    /// <c>IMediator</c>. The default <see cref="ISagaCommandDispatcher"/> calls it at most once per
    /// scope, with that scope's provider.
    /// </summary>
    /// <param name="services">The scoped service provider to resolve the mediator from.</param>
    public abstract ISagaCommandDispatcher CreateDispatcher(IServiceProvider services);

    /// <summary>
    /// Deserializes one of this source's commands and sends it through the assembly's own
    /// <c>IMediator</c>. The outbox bridge calls it for every saga command row it dispatches.
    /// </summary>
    /// <param name="typeName">The command type's <see cref="Type.FullName"/>.</param>
    /// <param name="payload">The serialized command.</param>
    /// <param name="services">The scoped service provider to resolve the serializer and mediator from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="NotSupportedException"><see cref="CanDispatchSerialized"/> is false.</exception>
    public virtual ValueTask DispatchSerializedAsync(
        string typeName,
        ReadOnlyMemory<byte> payload,
        IServiceProvider services,
        CancellationToken ct)
        => throw new NotSupportedException(
            $"The saga command source of assembly '{SagaCommandSourceBuilderExtensions.NameOf(this)}' cannot " +
            "dispatch serialized commands. The Saga generator implements this when the assembly references " +
            "ZeroAlloc.Serialisation.");
}
