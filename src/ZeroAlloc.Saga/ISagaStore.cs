namespace ZeroAlloc.Saga;

/// <summary>
/// Persists saga instances by correlation key. The default in-memory
/// implementation is process-local; production implementations should
/// persist to durable storage (SQL, Redis, etc.).
/// </summary>
public interface ISagaStore<TSaga, TKey>
    where TSaga : class, new()
    where TKey : notnull, IEquatable<TKey>
{
    /// <summary>
    /// Returns the saga for <paramref name="key"/> if one exists, or <c>null</c>.
    /// </summary>
    ValueTask<TSaga?> TryLoadAsync(TKey key, CancellationToken ct);

    /// <summary>
    /// Returns the saga for <paramref name="key"/>, creating a fresh instance
    /// if none exists. The instance returned is the canonical reference for
    /// the key — subsequent <see cref="TryLoadAsync"/> calls return the same
    /// object.
    /// </summary>
    ValueTask<TSaga> LoadOrCreateAsync(TKey key, CancellationToken ct);

    /// <summary>
    /// Persists the current state of <paramref name="saga"/> for <paramref name="key"/>.
    /// In-memory stores may be a no-op since the instance reference is the
    /// canonical store entry.
    /// </summary>
    ValueTask SaveAsync(TKey key, TSaga saga, CancellationToken ct);

    /// <summary>
    /// Removes the saga associated with <paramref name="key"/>, if any.
    /// Called when a saga reaches a terminal state.
    /// </summary>
    /// <remarks>
    /// Like <see cref="SaveAsync"/>, this ends a handler attempt, so a store that shares a
    /// transactional context with an <see cref="ISagaUnitOfWork"/> must commit the writes
    /// enlisted in the same DI scope, such as the outbox row of the step that completed the
    /// saga, in the same transaction as the removal. It must do so whether or not an instance
    /// exists: a saga that one event both starts and completes is never saved, but its step
    /// command still has to commit.
    /// </remarks>
    ValueTask RemoveAsync(TKey key, CancellationToken ct);
}
