namespace ZeroAlloc.Saga;

/// <summary>
/// Per-key serialization primitive. The saga runtime uses a lock manager
/// keyed on the correlation key so that handlers for the same saga
/// instance never run concurrently, while handlers for different saga
/// instances run in parallel.
/// </summary>
/// <remarks>
/// <para>
/// A key is tracked only while someone holds or waits for its lock. The last release
/// removes it, so memory stays proportional to the number of keys in use, not to every
/// key ever seen.
/// </para>
/// <para>
/// Acquiring an idle key allocates nothing once the manager has warmed up: the lock is
/// returned as a <see cref="SagaLock"/> struct, and the per-key entry behind it is pooled.
/// Waiting for a held key still allocates the wait.
/// </para>
/// </remarks>
public sealed class SagaLockManager<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    // Entries kept for reuse once their key is released. A bound keeps an idle manager small
    // after a burst of distinct keys; beyond it a released entry is left to the GC.
    private const int MaxPooledEntries = 64;

    // One gate guards the dictionary, the pool and every entry. Each critical section is a
    // dictionary operation, a pool step or a queue step, so a single gate is cheap, and it makes
    // "remove the key" and "join the key's queue" mutually exclusive by construction.
    private readonly System.Threading.Lock _gate = new();
    private readonly Dictionary<TKey, KeyLock> _held = new();
    private readonly Stack<KeyLock> _pool = new();

    /// <summary>
    /// Acquires the lock for <paramref name="key"/>, blocking asynchronously
    /// if another caller already holds it. Dispose the returned <see cref="SagaLock"/>
    /// exactly once to release the lock.
    /// </summary>
    public ValueTask<SagaLock> AcquireAsync(TKey key, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<SagaLock>(ct);

        Waiter waiter;
        lock (_gate)
        {
            if (!_held.TryGetValue(key, out var entry))
            {
                entry = _pool.Count > 0 ? _pool.Pop() : new KeyLock(this);
                entry.Key = key;
                _held.Add(key, entry);
                return new ValueTask<SagaLock>(new SagaLock(entry, entry.Generation));
            }

            waiter = entry.Enqueue(ct);
        }

        return WaitAsync(waiter, ct);
    }

    /// <summary>Number of keys currently held or waited on. Test hook for #247.</summary>
    internal int TrackedKeyCount
    {
        get
        {
            lock (_gate)
                return _held.Count;
        }
    }

    /// <summary>Number of released entries kept for reuse. Test hook for #250.</summary>
    internal int PooledEntryCount
    {
        get
        {
            lock (_gate)
                return _pool.Count;
        }
    }

    private static async ValueTask<SagaLock> WaitAsync(Waiter waiter, CancellationToken ct)
    {
        var registration = ct.CanBeCanceled
            ? ct.Register(static state => ((Waiter)state!).Cancel(), waiter)
            : default;
        await using (registration.ConfigureAwait(false))
        {
            return await waiter.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The lock for one key, present in the dictionary exactly while it is held. Each grant
    /// carries the entry's generation; every release advances it, so a releaser that was
    /// already disposed, or that outlived the entry's return to the pool, no longer matches.
    /// </summary>
    private sealed class KeyLock : SagaLockEntry
    {
        private readonly SagaLockManager<TKey> _owner;
        private LinkedList<Waiter>? _waiters;

        public KeyLock(SagaLockManager<TKey> owner) => _owner = owner;

        /// <summary>The key this entry locks while it is in the dictionary. Caller holds the gate.</summary>
        public TKey Key { get; set; } = default!;

        /// <summary>The generation the current holder was granted. Caller holds the gate.</summary>
        public long Generation { get; private set; }

        /// <summary>Queues a waiter. Caller holds the owner's gate.</summary>
        public Waiter Enqueue(CancellationToken ct)
        {
            var waiter = new Waiter(this, ct);
            _waiters ??= new LinkedList<Waiter>();
            waiter.Node = _waiters.AddLast(waiter);
            return waiter;
        }

        internal override void Release(long generation)
        {
            Waiter? next = null;
            long granted;
            lock (_owner._gate)
            {
                if (generation != Generation)
                    throw new InvalidOperationException("The saga lock was released more often than it was acquired.");

                // A long never repeats in practice, so a stale releaser can never match a later
                // grant of the same entry.
                granted = ++Generation;

                var first = _waiters?.First;
                if (first is not null)
                {
                    // Hand the lock straight to the next waiter. The entry stays in the
                    // dictionary, so no newcomer can slip in before the waiter resumes.
                    _waiters!.Remove(first);
                    next = first.Value;
                    next.Node = null;
                }
                else
                {
                    // Nobody holds or waits: forget the key and keep the entry for another one.
                    _owner._held.Remove(Key);
                    Key = default!;
                    if (_owner._pool.Count < MaxPooledEntries)
                        _owner._pool.Push(this);
                }
            }

            next?.TrySetResult(new SagaLock(this, granted));
        }

        /// <summary>Removes a cancelled waiter; false when it was already handed the lock.</summary>
        public bool TryRemove(Waiter waiter)
        {
            lock (_owner._gate)
            {
                if (waiter.Node is null)
                    return false;

                _waiters!.Remove(waiter.Node);
                waiter.Node = null;
                return true;
            }
        }
    }

    private sealed class Waiter : TaskCompletionSource<SagaLock>
    {
        private readonly KeyLock _entry;
        private readonly CancellationToken _ct;

        public Waiter(KeyLock entry, CancellationToken ct)
            : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            _entry = entry;
            _ct = ct;
        }

        /// <summary>Position in the wait queue; null once granted or cancelled.</summary>
        public LinkedListNode<Waiter>? Node { get; set; }

        public void Cancel()
        {
            // A waiter that was already granted the lock keeps it; the caller disposes it.
            if (_entry.TryRemove(this))
                TrySetCanceled(_ct);
        }
    }
}
