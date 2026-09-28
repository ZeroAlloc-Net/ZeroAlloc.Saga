using System;
using System.Collections.Generic;
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

    // The keys this store loaded and found no row for. EF's change tracker
    // remembers a row that was loaded, with the RowVersion the OCC check needs,
    // but it keeps nothing for a load that found no row. Without this set, a
    // later save or remove would query again, pick up a row another writer
    // created since, and overwrite or delete it. Redis and the ORM store treat
    // "found no row" as an observation in the same way.
    //
    // A key in neither this set nor the change tracker was never loaded, and
    // keeps the unconditional behaviour. The store is scoped like the DbContext
    // it wraps, so the set lives exactly as long as that change tracker.
    private readonly HashSet<TKey> _loadedAbsent = new();

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
        if (entity is null)
        {
            _loadedAbsent.Add(key);
            return null;
        }

        // The change tracker now holds the row and its RowVersion.
        _loadedAbsent.Remove(key);

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
        // GetEntityAsync returns the row this scope loaded from EF's change
        // tracker, with its original RowVersion in EF's snapshot, which is what
        // the OCC check on save needs. A row deleted by another writer since the
        // load therefore still updates here, and the update's RowVersion
        // predicate reports the conflict.
        var entity = await GetEntityAsync(key, ct).ConfigureAwait(false);
        ThrowIfCreatedSinceLoadedAbsent(key, entity);
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
            // A row another writer inserts before this commit collides on the
            // key, and CommitAsync reports that as a conflict.
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

        await CommitAsync(key, ct).ConfigureAwait(false);
        // The row is this scope's own now, tracked with its RowVersion.
        _loadedAbsent.Remove(key);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removing ends a handler attempt just as saving does, so this commits
    /// everything the attempt enlisted in the scoped <see cref="DbContext"/>,
    /// such as the outbox row of the step that completed the saga, whether or
    /// not a saga row exists. A saga that one event both starts and completes
    /// was never saved, so there is no row to delete, but its command still has
    /// to commit. A row this scope loaded is deleted against the RowVersion it
    /// was loaded with, so a row another writer changed or deleted since is a
    /// conflict, and the attempt's enlisted writes are discarded with it. When
    /// this scope loaded the key and found no row, a row another writer created
    /// since is a conflict too, and is kept.
    /// </remarks>
    /// <exception cref="EfCoreSagaConcurrencyException">
    /// Another writer changed, deleted or created the row since this scope
    /// loaded the key. The generated handler retries, as it does for a save.
    /// </exception>
    public async ValueTask RemoveAsync(TKey key, CancellationToken ct)
    {
        var entity = await GetEntityAsync(key, ct).ConfigureAwait(false);
        ThrowIfCreatedSinceLoadedAbsent(key, entity);
        if (entity is not null)
        {
            _context.Set<SagaInstanceEntity>().Remove(entity);
            _log.LogDebug("Removing saga row {SagaType}/{Key}", s_sagaTypeKey, key);
        }
        else
        {
            _log.LogDebug("No saga row to remove for {SagaType}/{Key}", s_sagaTypeKey, key);
        }

        await CommitAsync(key, ct).ConfigureAwait(false);
        // Gone as of this commit, which is an observation too.
        _loadedAbsent.Add(key);
    }

    /// <summary>
    /// Raises the conflict for a key this scope loaded and found no row for,
    /// when a row exists now. That row is another writer's: they created the
    /// saga after this scope's load. Saving over it or deleting it would lose
    /// their progress, so the attempt fails and the retry loads their saga.
    /// </summary>
    private void ThrowIfCreatedSinceLoadedAbsent(TKey key, SagaInstanceEntity? entity)
    {
        if (entity is null || !_loadedAbsent.Contains(key))
            return;

        var correlationKey = key.ToString() ?? string.Empty;
        _log.LogDebug(
            "Saga row {SagaType}/{Key} was created by another writer after this scope found none",
            s_sagaTypeKey, key);
        throw new EfCoreSagaConcurrencyException(
            s_sagaTypeKey,
            correlationKey,
            new DbUpdateConcurrencyException(
                $"This scope loaded saga '{s_sagaTypeKey}' with correlation key '{correlationKey}' " +
                "and found no row, and another writer has created the row since."));
    }

    /// <summary>
    /// The one place this store commits. Both <see cref="SaveAsync"/> and
    /// <see cref="RemoveAsync"/> end in it unconditionally, so no path through
    /// either can drop work enlisted in the scoped <see cref="DbContext"/>.
    /// </summary>
    private async Task CommitAsync(TKey key, CancellationToken ct)
    {
        try
        {
            await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex is not EfCoreSagaConcurrencyException)
        {
            // Re-thrown as a type implementing ISagaConcurrencyConflict so the
            // generator-emitted retry loop recognises it without EF Core's type
            // names being compiled into the generator. The wrapper derives from
            // DbUpdateConcurrencyException, so callers already catching that — or
            // DbUpdateException — are unaffected.
            throw new EfCoreSagaConcurrencyException(s_sagaTypeKey, key.ToString() ?? string.Empty, ex);
        }
    }

    private ValueTask<SagaInstanceEntity?> GetEntityAsync(TKey key, CancellationToken ct)
    {
        // FindAsync returns the instance this DbContext already tracks for the
        // key, and queries only when it tracks none. Once a scope has loaded a
        // row, a later save or remove works on that instance and its original
        // RowVersion, even if another writer has deleted the row since: the
        // UPDATE or DELETE then affects no row and raises the conflict. A fresh
        // query would instead return null for the deleted row and turn the
        // save into an insert, or the remove into a silent no-op.
        // Key order matches the composite key AddSagas() configures.
        var keyStr = key.ToString() ?? string.Empty;
        return _context.Set<SagaInstanceEntity>().FindAsync([s_sagaTypeKey, keyStr], ct);
    }
}
