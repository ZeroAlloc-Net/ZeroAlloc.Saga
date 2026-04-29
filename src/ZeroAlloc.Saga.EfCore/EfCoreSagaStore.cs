using Microsoft.EntityFrameworkCore;

namespace ZeroAlloc.Saga.EfCore;

// MA0025: NotImplementedException is intentional here — PR 2 ships only the
// type skeleton. The full implementation lands in PR 3 of the Phase 2 EfCore
// campaign; the stub form is what lets the generator and DI wiring reference
// EfCoreSagaStore<,> by name today.
#pragma warning disable MA0025

/// <summary>
/// EF Core-backed <see cref="ISagaStore{TSaga,TKey}"/> implementation. Uses
/// row-version OCC against the shared <c>SagaInstance</c> table for safe
/// concurrent saves; the generator-emitted handler wraps each step in a
/// retry loop on <see cref="DbUpdateConcurrencyException"/>.
/// </summary>
/// <remarks>
/// PR 2 ships only this skeleton — every method throws
/// <see cref="NotImplementedException"/>. The full implementation lands in
/// PR 3 of the Phase 2 EfCore campaign; the type, generic constraints, and
/// public surface are pinned in this PR so the generator and DI wiring can
/// reference them without forward-declaration churn.
/// </remarks>
public sealed class EfCoreSagaStore<TSaga, TKey> : ISagaStore<TSaga, TKey>
    where TSaga : class, ISagaPersistableState, new()
    where TKey : notnull, IEquatable<TKey>
{
    // Implementation in PR 3.

    /// <inheritdoc />
    public ValueTask<TSaga?> TryLoadAsync(TKey key, CancellationToken ct)
        => throw new NotImplementedException("Implementation lands in PR 3");

    /// <inheritdoc />
    public ValueTask<TSaga> LoadOrCreateAsync(TKey key, CancellationToken ct)
        => throw new NotImplementedException("Implementation lands in PR 3");

    /// <inheritdoc />
    public ValueTask SaveAsync(TKey key, TSaga saga, CancellationToken ct)
        => throw new NotImplementedException("Implementation lands in PR 3");

    /// <inheritdoc />
    public ValueTask RemoveAsync(TKey key, CancellationToken ct)
        => throw new NotImplementedException("Implementation lands in PR 3");
}
