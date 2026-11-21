namespace ZeroAlloc.Saga;

/// <summary>
/// A held per-key lock from <see cref="SagaLockManager{TKey}.AcquireAsync"/>. Dispose it exactly
/// once to release the lock.
/// </summary>
/// <remarks>
/// A struct, so acquiring an idle key allocates nothing. Dispose it through a
/// <see langword="using"/> declaration rather than a copy held elsewhere: every copy refers to the
/// same grant, and a second <see cref="Dispose"/> of any copy throws
/// <see cref="InvalidOperationException"/>. A <see langword="default"/> instance holds nothing,
/// and disposing it does nothing.
/// </remarks>
public readonly struct SagaLock : IDisposable
{
    private readonly SagaLockEntry? _entry;
    private readonly long _generation;

    internal SagaLock(SagaLockEntry entry, long generation)
    {
        _entry = entry;
        _generation = generation;
    }

    /// <summary>Releases the lock, handing it to the next waiter for the same key if there is one.</summary>
    /// <exception cref="InvalidOperationException">This grant was already released.</exception>
    public void Dispose() => _entry?.Release(_generation);
}
