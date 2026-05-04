using StackExchange.Redis;

namespace ZeroAlloc.Saga.Redis;

/// <summary>
/// Extension point that lets sibling packages (most notably
/// <c>ZeroAlloc.Saga.Outbox.Redis</c>) contribute commands to the saga store's
/// <c>MULTI</c> batch so their writes commit atomically with the saga state save.
/// </summary>
/// <remarks>
/// All registered contributors are resolved from the same DI scope as the
/// <see cref="RedisSagaStore{TSaga,TKey}"/>. For each <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/>
/// call, after the store opens its <see cref="ITransaction"/> and queues the saga state
/// HSET, every contributor is invoked once with that transaction. <c>EXEC</c> then commits
/// all queued commands atomically. If <c>EXEC</c> aborts (WATCH detected concurrent change),
/// the contributor's writes roll back together with the saga update — exactly the contract
/// <c>Saga.Outbox</c>'s atomic-dispatch story relies on.
/// </remarks>
public interface IRedisSagaTransactionContributor
{
    /// <summary>
    /// Queue commands on <paramref name="transaction"/>. The transaction is the saga store's
    /// <c>MULTI</c> batch; do not call <c>ExecuteAsync</c> on it — the saga store does that.
    /// </summary>
    void Contribute(ITransaction transaction);
}
