using System.Collections.Concurrent;

namespace ZeroAlloc.Saga;

/// <summary>
/// Per-key serialization primitive. The saga runtime uses a lock manager
/// keyed on the correlation key so that handlers for the same saga
/// instance never run concurrently, while handlers for different saga
/// instances run in parallel.
/// </summary>
public sealed class SagaLockManager<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Acquires the lock for <paramref name="key"/>, blocking asynchronously
    /// if another caller already holds it. The returned disposable releases
    /// the lock when disposed.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(TKey key, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(sem);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        public Releaser(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() => _sem.Release();
    }
}
