using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga;

/// <summary>
/// Indirection layer between Saga's generated event handlers and the actual command-dispatch
/// mechanism. Default implementation (<see cref="MediatorSagaCommandDispatcher"/>) forwards
/// to <see cref="IMediator.Send"/>; the <c>ZeroAlloc.Saga.Outbox</c> package supplies an
/// alternative implementation that writes to a transactional outbox so the dispatch commits
/// atomically with the saga state save.
/// </summary>
public interface ISagaCommandDispatcher
{
    /// <summary>Dispatch a saga step command. Implementations must be thread-safe.</summary>
    ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>;
}
