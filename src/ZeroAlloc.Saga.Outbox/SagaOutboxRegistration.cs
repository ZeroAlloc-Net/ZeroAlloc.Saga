using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// What <see cref="SagaOutboxBuilderExtensions.WithOutbox"/> registered, for
/// <see cref="SagaOutboxStartupCheck"/>: the saga command type names it registered a dispatcher
/// for, one <see cref="SagaCommandSource"/> per assembly that declares sagas, and the assemblies
/// whose commands cannot be dispatched from the outbox. Its registration also marks
/// <c>WithOutbox()</c> as applied, so a second call registers nothing twice.
/// </summary>
/// <remarks>
/// Filled while the service collection is built, through
/// <see cref="SagaCommandSourceBuilderExtensions.ForEachCommandSource"/>, and only read once the
/// container exists.
/// </remarks>
internal sealed class SagaOutboxRegistration
{
    private readonly Dictionary<string, SagaCommandSource> _sourceByTypeName = new(StringComparer.Ordinal);
    private readonly List<string> _typeNames = [];
    private readonly List<string> _assembliesWithoutSerialisation = [];

    /// <summary>The saga command type names, as <c>Type.FullName</c>.</summary>
    public IReadOnlyList<string> TypeNames => _typeNames;

    /// <summary>How many <see cref="SagaCommandSource"/>s were added: one per assembly with sagas.</summary>
    public int SourceCount { get; private set; }

    /// <summary>
    /// The assemblies whose sagas were registered but whose commands cannot be dispatched from the
    /// outbox, because the assembly does not reference ZeroAlloc.Serialisation.
    /// </summary>
    public IReadOnlyList<string> AssembliesWithoutSerialisation => _assembliesWithoutSerialisation;

    /// <summary>
    /// Records <paramref name="source"/> and returns the type names to register an outbox
    /// dispatcher for: all of its command types, or none when it cannot dispatch serialized
    /// commands.
    /// </summary>
    public IReadOnlyList<string> Add(SagaCommandSource source)
    {
        SourceCount++;
        if (!source.CanDispatchSerialized)
        {
            _assembliesWithoutSerialisation.Add(source.GetType().Assembly.GetName().Name ?? source.GetType().FullName!);
            return [];
        }

        var added = new List<string>(source.CommandTypes.Count);
        foreach (var type in source.CommandTypes)
        {
            var typeName = type.FullName!;
            // AddCommandSource rejects a command type listed by two sources, so each name is new.
            _sourceByTypeName.Add(typeName, source);
            _typeNames.Add(typeName);
            added.Add(typeName);
        }

        return added;
    }

    /// <summary>
    /// The default <see cref="SagaCommandRegistryDispatcher"/>: dispatches through the source of
    /// the assembly whose sagas return the command.
    /// </summary>
    public ValueTask DispatchAsync(string typeName, ReadOnlyMemory<byte> bytes, IServiceProvider services, CancellationToken ct)
    {
        if (!_sourceByTypeName.TryGetValue(typeName, out var source))
        {
            throw new InvalidOperationException(
                $"Unknown saga command type '{typeName}'. No registered saga returns a command of that type.");
        }

        return source.DispatchSerializedAsync(typeName, bytes, services, ct);
    }
}
