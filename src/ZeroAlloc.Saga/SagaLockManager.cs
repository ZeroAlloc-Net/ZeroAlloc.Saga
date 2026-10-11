namespace ZeroAlloc.Saga;

/// <summary>
/// Per-key serialization primitive. The saga runtime uses a lock manager
/// keyed on the correlation key so that handlers for the same saga
/// instance never run concurrently, while handlers for different saga
/// instances run in parallel.
/// </summary>
/// <remarks>
/// A key is tracked only while someone holds or waits for its lock. The last release
/// removes it, so memory stays proportional to the number of keys in use, not to every
/// key ever seen.
/// </remarks>
public sealed class SagaLockManager<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    // One gate guards the dictionary and every entry in it. Each critical section is a
    // dictionary operation or a queue step, so a single gate is cheap, and it makes
    // "remove the key" and "join the key's queue" mutually exclusive by construction.
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly Dictionary<TKey, KeyLock> _held = new();

    /// <summary>
    /// Acquires the lock for <paramref name="key"/>, blocking asynchronously
    /// if another caller already holds it. The returned disposable releases
    /// the lock when disposed; dispose it exactly once.
    /// </summary>
    public ValueTask<IDisposable> AcquireAsync(TKey key, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled<IDisposable>(ct);

        Waiter waiter;
        lock (_gate)
        {
            if (!_held.TryGetValue(key, out var entry))
            {
                entry = new KeyLock(this, key);
                _held.Add(key, entry);
                return new ValueTask<IDisposable>(entry);
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

    private static async ValueTask<IDisposable> WaitAsync(Waiter waiter, CancellationToken ct)
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
    /// The lock for one key, present in the dictionary exactly while it is held. The holder
    /// receives the entry itself as its releaser, so a lock costs one allocation per
    /// acquisition of an idle key, the same count as the releaser it replaces.
    /// </summary>
    private sealed class KeyLock : IDisposable
    {
        private readonly SagaLockManager<TKey> _owner;
        private readonly TKey _key;
        private LinkedList<Waiter>? _waiters;
        private bool _released;

        public KeyLock(SagaLockManager<TKey> owner, TKey key)
        {
            _owner = owner;
            _key = key;
        }

        /// <summary>Queues a waiter. Caller holds the owner's gate.</summary>
        public Waiter Enqueue(CancellationToken ct)
        {
            var waiter = new Waiter(this, ct);
            _waiters ??= new LinkedList<Waiter>();
            waiter.Node = _waiters.AddLast(waiter);
            return waiter;
        }

        public void Dispose()
        {
            Waiter? next = null;
            lock (_owner._gate)
            {
                if (_released)
                    throw new InvalidOperationException("The saga lock was released more often than it was acquired.");

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
                    // Nobody holds or waits: forget the key. A later acquirer creates a
                    // fresh entry; this one is never handed out again.
                    _released = true;
                    _owner._held.Remove(_key);
                }
            }

            next?.TrySetResult(this);
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

    private sealed class Waiter : TaskCompletionSource<IDisposable>
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
