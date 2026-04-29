namespace ZeroAlloc.Saga;

/// <summary>
/// Implemented by generator-emitted code per saga. Knows the compensation
/// cascade chain and dispatches it via <see cref="Mediator.IMediator"/>.
/// Used by <see cref="ISagaManager{TSaga,TKey}.CompensateAsync"/> for
/// operator-initiated compensation.
/// </summary>
public interface ISagaCompensationDispatcher<TSaga>
    where TSaga : class, new()
{
    /// <summary>
    /// Walks the saga's reverse-cascade compensation chain starting from
    /// the current FSM state and dispatches the compensating commands.
    /// </summary>
    ValueTask CompensateAsync(TSaga saga, CancellationToken ct);
}
