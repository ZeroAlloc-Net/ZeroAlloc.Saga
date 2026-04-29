namespace ZeroAlloc.Saga;

/// <summary>
/// Default <see cref="ISagaManager{TSaga,TKey}"/> implementation. Resolves
/// the per-saga store, lock manager, and compensation dispatcher from DI.
/// </summary>
public sealed class SagaManager<TSaga, TKey> : ISagaManager<TSaga, TKey>
    where TSaga : class, new()
    where TKey : notnull, IEquatable<TKey>
{
    private readonly ISagaStore<TSaga, TKey> _store;
    private readonly SagaLockManager<TKey> _locks;
    private readonly ISagaCompensationDispatcher<TSaga> _compensation;

    public SagaManager(
        ISagaStore<TSaga, TKey> store,
        SagaLockManager<TKey> locks,
        ISagaCompensationDispatcher<TSaga> compensation)
    {
        _store = store;
        _locks = locks;
        _compensation = compensation;
    }

    /// <inheritdoc/>
    public ValueTask<TSaga?> GetAsync(TKey key, CancellationToken ct)
        => _store.TryLoadAsync(key, ct);

    /// <inheritdoc/>
    public async ValueTask CompensateAsync(TKey key, CancellationToken ct)
    {
        using var _ = await _locks.AcquireAsync(key, ct).ConfigureAwait(false);
        var saga = await _store.TryLoadAsync(key, ct).ConfigureAwait(false);
        if (saga is null) return;

        await _compensation.CompensateAsync(saga, ct).ConfigureAwait(false);
        await _store.RemoveAsync(key, ct).ConfigureAwait(false);
    }
}
