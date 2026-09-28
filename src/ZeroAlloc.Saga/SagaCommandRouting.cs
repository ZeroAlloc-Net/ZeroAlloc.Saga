using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Saga;

/// <summary>
/// The registered <see cref="SagaCommandSource"/>s and which one dispatches each command type.
/// Built once per container, from every <see cref="SagaCommandSource"/> singleton.
/// </summary>
internal sealed class SagaCommandRouting
{
    private readonly Dictionary<Type, int> _sourceByType = new();

    public SagaCommandRouting(IEnumerable<SagaCommandSource> sources)
    {
        var list = new List<SagaCommandSource>(sources);
        Sources = list.ToArray();
        for (var i = 0; i < Sources.Length; i++)
        {
            foreach (var type in Sources[i].CommandTypes)
            {
                // AddCommandSource already rejects this; a source registered around it would not be.
                if (_sourceByType.TryGetValue(type, out var owner))
                {
                    throw new InvalidOperationException(
                        SagaCommandSourceBuilderExtensions.ConflictMessage(type, Sources[owner], Sources[i]));
                }

                _sourceByType.Add(type, i);
            }
        }
    }

    public SagaCommandSource[] Sources { get; }

    /// <summary>
    /// The default <see cref="ISagaCommandDispatcher"/>. With a single source it is that source's
    /// own dispatcher, exactly as before sources existed; with more it routes by command type.
    /// </summary>
    public static ISagaCommandDispatcher CreateDefaultDispatcher(IServiceProvider services)
    {
        var routing = services.GetRequiredService<SagaCommandRouting>();
        return routing.Sources.Length == 1
            ? routing.Sources[0].CreateDispatcher(services)
            : new RoutingSagaCommandDispatcher(routing, services);
    }

    public bool TryGetSourceIndex(Type commandType, out int index) => _sourceByType.TryGetValue(commandType, out index);
}
