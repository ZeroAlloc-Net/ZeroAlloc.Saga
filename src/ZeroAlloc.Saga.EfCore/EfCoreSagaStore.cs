using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZeroAlloc.Saga.EfCore;

/// <summary>
/// EF Core-backed <see cref="ISagaStore{TSaga,TKey}"/> implementation. Persists
/// each saga instance as a row in the shared <c>SagaInstance</c> table, keyed
/// by <c>(SagaType, CorrelationKey)</c>. Optimistic concurrency is driven by
/// the <see cref="SagaInstanceEntity.RowVersion"/> column, mapped via
/// <c>IsConcurrencyToken()</c> with a manual <see cref="Guid.NewGuid"/>
/// rotation per save (SQLite has no native row-version, so we manage the
/// token in code; EF still includes the OLD value in the WHERE clause for
/// the OCC check). <see cref="SaveAsync"/> propagates
/// <see cref="DbUpdateConcurrencyException"/> to the caller so the
/// generator-emitted notification handler can retry the entire fire/dispatch/save flow.
/// </summary>
/// <typeparam name="TSaga">The user's saga class. Must be partial and decorated with
/// <see cref="SagaAttribute"/> so the generator emits its
/// <see cref="ISagaPersistableState"/> implementation. The constraint here matches
/// <see cref="ISagaStore{TSaga,TKey}"/>'s looser contract; the constructor enforces
/// the persistable interface at runtime so the generic registration stays AOT-clean
/// without an extra <c>MakeGenericType</c> step.</typeparam>
/// <typeparam name="TKey">The correlation key type.</typeparam>
public sealed class EfCoreSagaStore<TSaga, TKey> : ISagaStore<TSaga, TKey>
    where TSaga : class, new()
    where TKey : notnull, IEquatable<TKey>
{
    private static readonly string s_sagaTypeKey = typeof(TSaga).FullName ?? typeof(TSaga).Name;

    private readonly DbContext _context;
    private readonly ILogger _log;

    /// <summary>
    /// Constructs the store. The <paramref name="context"/> is expected to be
    /// the user's <c>TContext</c> resolved through the DI container; the
    /// <c>WithEfCoreStore&lt;TContext&gt;()</c> extension registers a
    /// <see cref="DbContext"/>-typed alias so this generic store does not need
    /// to know <c>TContext</c> at compile time.
    /// </summary>
    public EfCoreSagaStore(DbContext context, ILogger<EfCoreSagaStore<TSaga, TKey>>? log = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (!typeof(ISagaPersistableState).IsAssignableFrom(typeof(TSaga)))
        {
            throw new InvalidOperationException(
                $"Saga '{typeof(TSaga).FullName}' does not implement ISagaPersistableState. " +
                "The Saga generator should emit this interface automatically; ensure the " +
                "[Saga] attribute is present and the generator is wired in the saga's project.");
        }
        _log = log ?? NullLogger<EfCoreSagaStore<TSaga, TKey>>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<TSaga?> TryLoadAsync(TKey key, CancellationToken ct)
    {
        var entity = await GetEntityAsync(key, ct).ConfigureAwait(false);
        if (entity is null) return null;

        var saga = new TSaga();
        var persistable = (ISagaPersistableState)saga;
        persistable.Restore(entity.State);
        persistable.SetFsmStateFromName(entity.CurrentFsmState);
        return saga;
    }

    /// <inheritdoc />
    public async ValueTask<TSaga> LoadOrCreateAsync(TKey key, CancellationToken ct)
    {
        var existing = await TryLoadAsync(key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _log.LogDebug("Saga {SagaType} loaded for key {Key}", s_sagaTypeKey, key);
            return existing;
        }
        _log.LogDebug("Saga {SagaType} created (no existing row) for key {Key}", s_sagaTypeKey, key);
        return new TSaga();
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(TKey key, TSaga saga, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saga);

        var persistable = (ISagaPersistableState)saga;
        // GetEntityAsync hits EF's ChangeTracker cache (no DB roundtrip)
        // because the DbContext is request-scoped and TryLoadAsync was the
        // last op that materialised this row inside LoadOrCreateAsync. The
        // cached entry is returned with the original RowVersion in EF's
        // snapshot, which is what we need for the OCC check on save.
        var entity = await GetEntityAsync(key, ct).ConfigureAwait(false);
        var newState = persistable.Snapshot();
        var newFsmState = persistable.CurrentFsmStateName;
        var now = DateTimeOffset.UtcNow;

        if (entity is null)
        {
            _context.Set<SagaInstanceEntity>().Add(new SagaInstanceEntity
            {
                SagaType = s_sagaTypeKey,
                CorrelationKey = key.ToString() ?? string.Empty,
                State = newState,
                CurrentFsmState = newFsmState,
                CreatedAt = now,
                UpdatedAt = now,
                // Initialize the concurrency token. EF picks up the new value
                // on INSERT and includes it in subsequent UPDATE WHERE clauses.
                RowVersion = Guid.NewGuid().ToByteArray(),
            });
            _log.LogDebug("Inserting new saga row {SagaType}/{Key}", s_sagaTypeKey, key);
        }
        else
        {
            entity.State = newState;
            entity.CurrentFsmState = newFsmState;
            entity.UpdatedAt = now;
            // Rotate the concurrency token. Because RowVersion is mapped as a
            // concurrency token (not IsRowVersion), EF includes the OLD value
            // in the UPDATE WHERE clause — affecting zero rows when another
            // writer changed the value underneath, surfacing as
            // DbUpdateConcurrencyException.
            entity.RowVersion = Guid.NewGuid().ToByteArray();
            _log.LogDebug("Updating saga row {SagaType}/{Key}", s_sagaTypeKey, key);
        }

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        // DbUpdateConcurrencyException propagates — caller (generator-emitted
        // handler) catches and retries inside its OCC loop.
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(TKey key, CancellationToken ct)
    {
        var entity = await GetEntityAsync(key, ct).ConfigureAwait(false);
        if (entity is null) return;
        _context.Set<SagaInstanceEntity>().Remove(entity);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        _log.LogDebug("Removed saga row {SagaType}/{Key}", s_sagaTypeKey, key);
    }

    private Task<SagaInstanceEntity?> GetEntityAsync(TKey key, CancellationToken ct)
    {
        var keyStr = key.ToString() ?? string.Empty;
        return _context.Set<SagaInstanceEntity>()
            .AsTracking()
            .FirstOrDefaultAsync(e => e.SagaType == s_sagaTypeKey && e.CorrelationKey == keyStr, ct);
    }
}
