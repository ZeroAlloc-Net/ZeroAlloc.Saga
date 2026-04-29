namespace ZeroAlloc.Saga;

/// <summary>
/// Operator-facing entry point for inspecting and forcefully compensating
/// a running saga. Resolved from DI; one closed-generic registration is
/// emitted per <see cref="SagaAttribute"/>.
/// </summary>
public interface ISagaManager<TSaga, TKey>
    where TSaga : class, new()
    where TKey : notnull, IEquatable<TKey>
{
    /// <summary>
    /// Returns the saga instance for <paramref name="key"/>, or <c>null</c>
    /// if no saga has been started for that key.
    /// </summary>
    ValueTask<TSaga?> GetAsync(TKey key, CancellationToken ct);

    /// <summary>
    /// Drives the reverse-cascade compensation chain for the saga at
    /// <paramref name="key"/> and removes it from the store. No-op if no
    /// saga exists for the key.
    /// </summary>
    ValueTask CompensateAsync(TKey key, CancellationToken ct);
}
