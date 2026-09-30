using System.Data.Async;

namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// Extension point that lets a sibling package write inside the ORM saga store's transaction,
/// so its rows commit or roll back together with the saga row. <c>ZeroAlloc.Saga.Outbox.Orm</c>
/// uses it to commit a saga step's outbox rows atomically with the saga state.
/// </summary>
/// <remarks>
/// <para>
/// Register implementations as scoped services. The store resolves every registered
/// contributor from its own scope. Each <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/> and
/// <see cref="ISagaStore{TSaga,TKey}.RemoveAsync"/> begins a transaction on the application's
/// <see cref="IAsyncDbConnection"/>, runs its insert, update or delete in it, calls every
/// contributor once with that transaction, and commits. A removal that finds no row to delete
/// still calls the contributors and commits.
/// </para>
/// <para>
/// When the saga write loses an optimistic-concurrency check, the store rolls back without
/// calling the contributors. When a contributor throws, the store rolls back, so the saga row
/// is not written either, and the exception propagates.
/// </para>
/// </remarks>
public interface IOrmSagaTransactionContributor
{
    /// <summary>
    /// Writes inside <paramref name="transaction"/>. Run each command on
    /// <see cref="IAsyncDbTransaction.Connection"/> with its transaction set to
    /// <paramref name="transaction"/>. Do not commit or roll back: the saga store does that.
    /// </summary>
    /// <param name="transaction">The saga store's open transaction.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the writes have been issued.</returns>
    ValueTask ContributeAsync(IAsyncDbTransaction transaction, CancellationToken ct);
}
