using System.Collections.Concurrent;

namespace ZeroAlloc.Saga;

/// <summary>
/// Process-local in-memory <see cref="ISagaStore{TSaga,TKey}"/>. Suitable
/// for tests, prototypes, and single-process deployments. Saga instances
/// are stored by reference, so <see cref="SaveAsync"/> is a no-op —
/// in-place mutations are already visible to subsequent loads.
/// </summary>
public sealed class InMemorySagaStore<TSaga, TKey> : ISagaStore<TSaga, TKey>
    where TSaga : class, new()
    where TKey : notnull, IEquatable<TKey>
{
    private readonly ConcurrentDictionary<TKey, TSaga> _instances = new();

    /// <inheritdoc/>
    public ValueTask<TSaga?> TryLoadAsync(TKey key, CancellationToken ct)
        => new(_instances.TryGetValue(key, out var saga) ? saga : null);

    /// <inheritdoc/>
    public ValueTask<TSaga> LoadOrCreateAsync(TKey key, CancellationToken ct)
        => new(_instances.GetOrAdd(key, static _ => new TSaga()));

    /// <inheritdoc/>
    public ValueTask SaveAsync(TKey key, TSaga saga, CancellationToken ct)
        => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask RemoveAsync(TKey key, CancellationToken ct)
    {
        _instances.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }
}
