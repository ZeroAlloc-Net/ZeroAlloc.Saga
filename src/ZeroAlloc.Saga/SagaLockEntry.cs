namespace ZeroAlloc.Saga;

/// <summary>
/// The per-key entry behind a <see cref="SagaLock"/>. Non-generic so that <see cref="SagaLock"/>
/// does not depend on the manager's key type.
/// </summary>
internal abstract class SagaLockEntry
{
    /// <summary>Releases the grant identified by <paramref name="generation"/>.</summary>
    internal abstract void Release(int generation);
}
