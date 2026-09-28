using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga;

/// <summary>
/// Default <see cref="ISagaCommandDispatcher"/> when sagas are declared in more than one assembly.
/// Sends each command through the dispatcher of the assembly whose sagas return it, creating each
/// assembly's dispatcher on first use in the scope.
/// </summary>
internal sealed class RoutingSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly SagaCommandRouting _routing;
    private readonly IServiceProvider _services;
    private readonly ISagaCommandDispatcher?[] _dispatchers;

    public RoutingSagaCommandDispatcher(SagaCommandRouting routing, IServiceProvider services)
    {
        _routing = routing;
        _services = services;
        _dispatchers = new ISagaCommandDispatcher?[routing.Sources.Length];
    }

    public ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        // Generated saga handlers pass the step method's declared return type, which is the type
        // the source lists. The runtime type covers a caller that passes a base-typed variable.
        if (!_routing.TryGetSourceIndex(typeof(TCommand), out var index)
            && (cmd is null || !_routing.TryGetSourceIndex(cmd.GetType(), out index)))
        {
            throw new InvalidOperationException(
                $"No saga command source dispatches command type '{typeof(TCommand).FullName}'. Saga commands " +
                "are dispatched by the assembly whose [Saga] classes return them; register those sagas with " +
                "their generator-emitted With{Saga}() on this service collection.");
        }

        // Scoped, so a race here at worst creates the stateless per-assembly dispatcher twice.
        var dispatcher = _dispatchers[index] ??= _routing.Sources[index].CreateDispatcher(_services);
        return dispatcher.DispatchAsync(cmd, ct);
    }
}
