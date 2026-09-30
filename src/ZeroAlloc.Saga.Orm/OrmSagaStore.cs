using System.Data;
using System.Data.Async;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// Durable <see cref="ISagaStore{TSaga,TKey}"/> backed by ZeroAlloc.ORM. Every
/// saga instance lives in one shared <c>SagaInstance</c> table, keyed by
/// <c>(SagaType, CorrelationKey)</c> and guarded by a rotating row version.
/// </summary>
/// <remarks>
/// <para>
/// The persistence model matches the EF Core backend exactly — same table, same
/// columns, same optimistic-concurrency scheme, same "terminal states delete the
/// row" rule — so the two are interchangeable from the saga's point of view.
/// What differs is the machinery: the ORM emits its SQL at compile time, so
/// there is no change tracker, no model builder, and nothing that reflects at
/// runtime.
/// </para>
/// <para>
/// Without a change tracker the concurrency token has to be threaded explicitly.
/// <see cref="SaveAsync"/> reads the current row version and passes it as the
/// <c>WHERE</c> predicate of the update, so the check happens inside the single
/// statement the database executes rather than in a read-then-write window here.
/// <see cref="RemoveAsync"/> deletes against the same predicate.
/// </para>
/// <para>
/// Each save and removal is one transaction on the application's connection. The registered
/// <see cref="IOrmSagaTransactionContributor"/>s write into it after the saga statement, and it
/// commits once, so their rows and the saga row are committed or discarded together.
/// </para>
/// </remarks>
/// <typeparam name="TSaga">The saga type. Must implement <c>ISagaPersistableState</c>, which the Saga generator emits.</typeparam>
/// <typeparam name="TKey">The correlation key type.</typeparam>
public sealed class OrmSagaStore<TSaga, TKey> : ISagaStore<TSaga, TKey>
    where TSaga : class, new()
    where TKey : notnull, IEquatable<TKey>
{
    private static readonly string s_sagaTypeKey = typeof(TSaga).FullName ?? typeof(TSaga).Name;

    private readonly SagaInstanceRepository _repo;
    private readonly IOrmSagaTransactionContributor[] _contributors;
    private readonly ILogger _log;

    // Row versions as they were when this store last read or wrote each saga.
    // This is the store's unit of work, and it is what makes the concurrency
    // check meaningful: the predicate has to carry the version observed at
    // LOAD time, not one re-read at save time. Re-reading would compare the
    // row against itself and guard only the instant inside SaveAsync, letting
    // two writers that both loaded and then saved overwrite each other -- the
    // exact lost update the row version exists to prevent.
    //
    // A null value records that the load found no row. That is an observation
    // too: a remove must not then delete a row another writer created since.
    // A key with no entry was never loaded by this store.
    //
    // The store is scoped, so this dictionary lives as long as the request
    // that owns it. That is the same unit-of-work boundary EF Core's change
    // tracker gives the EF backend, and like a DbContext it is not safe to
    // share a single instance across concurrent work.
    private readonly Dictionary<TKey, byte[]?> _loadedVersions = new();

    /// <summary>
    /// Creates a store over the supplied repository.
    /// </summary>
    /// <param name="repo">Repository bound to the application's connection.</param>
    /// <param name="contributors">
    /// The scope's <see cref="IOrmSagaTransactionContributor"/>s, which write inside every save
    /// and removal's transaction.
    /// </param>
    /// <param name="log">Optional logger.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TSaga"/> does not implement <c>ISagaPersistableState</c>.
    /// </exception>
    internal OrmSagaStore(
        SagaInstanceRepository repo,
        IEnumerable<IOrmSagaTransactionContributor> contributors,
        ILogger<OrmSagaStore<TSaga, TKey>>? log = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        ArgumentNullException.ThrowIfNull(contributors);
        _contributors = contributors.ToArray();

        // Same guard the EF Core store applies. TSaga's constraint here is the
        // looser one ISagaStore declares, so the persistable contract has to be
        // checked at construction rather than by the compiler.
        if (!typeof(ISagaPersistableState).IsAssignableFrom(typeof(TSaga)))
        {
            throw new InvalidOperationException(
                $"Saga '{typeof(TSaga).FullName}' does not implement ISagaPersistableState. " +
                "The Saga generator should emit this interface automatically; ensure the " +
                "[Saga] attribute is present and the generator is wired in the saga's project.");
        }

        _log = log ?? NullLogger<OrmSagaStore<TSaga, TKey>>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<TSaga?> TryLoadAsync(TKey key, CancellationToken ct)
    {
        var row = await _repo.GetAsync(s_sagaTypeKey, KeyOf(key), ct).ConfigureAwait(false);
        if (row is null)
        {
            // Absent now means "insert on save". Replace any version from an
            // earlier read so a row deleted behind our back is not still
            // treated as an update.
            _loadedVersions[key] = null;
            return null;
        }

        _loadedVersions[key] = row.RowVersion;
        return Rehydrate(row);
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
    /// <remarks>
    /// The write runs in a transaction on the application's connection, together with every
    /// registered <see cref="IOrmSagaTransactionContributor"/>, and commits once. A conflict or
    /// any other failure rolls the whole transaction back.
    /// </remarks>
    /// <exception cref="OrmSagaConcurrencyException">
    /// Another writer changed the row first. The generated handler recognises
    /// this through <see cref="ISagaConcurrencyConflict"/> and retries.
    /// </exception>
    public async ValueTask SaveAsync(TKey key, TSaga saga, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saga);

        var persistable = (ISagaPersistableState)saga;
        var correlationKey = KeyOf(key);
        var newState = persistable.Snapshot();
        var newFsmState = persistable.CurrentFsmStateName;
        var now = DateTimeOffset.UtcNow;
        var newRowVersion = NewRowVersion();

        // Decide insert vs update from what this store has seen, not from a
        // fresh read. See _loadedVersions for why re-reading here would defeat
        // the concurrency check entirely.
        _loadedVersions.TryGetValue(key, out var expectedRowVersion);

        var openedHere = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var tx = await _repo.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                try
                {
                    await WriteInTransactionAsync(
                        key, correlationKey, newState, newFsmState, newRowVersion,
                        expectedRowVersion, now, tx, ct).ConfigureAwait(false);
                    await ContributeAndCommitAsync(tx, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await RollbackAsync(tx, ex).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            await CloseAsync(openedHere).ConfigureAwait(false);
        }

        // Our write is now the one others must match against. Only a committed
        // write moves it: a rolled-back insert or update never happened.
        _loadedVersions[key] = newRowVersion;
    }

    private async ValueTask WriteInTransactionAsync(
        TKey key, string correlationKey, byte[] newState, string newFsmState, byte[] newRowVersion,
        byte[]? expectedRowVersion, DateTimeOffset now, IAsyncDbTransaction tx, CancellationToken ct)
    {
        if (expectedRowVersion is null)
        {
            _log.LogDebug("Inserting new saga row {SagaType}/{Key}", s_sagaTypeKey, key);
            try
            {
                await _repo.InsertAsync(
                    s_sagaTypeKey, correlationKey, newState, newFsmState,
                    newRowVersion, now, now, tx, ct).ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                // Losing the insert race means another writer created this
                // instance first. That is a conflict, not a failure: reloading
                // and retrying will find their row. The ORM surfaces the
                // primary-key violation as a provider-specific DbException, so
                // every DbException from the insert is reported as a conflict,
                // as the EF Core store does for DbUpdateException. A genuine
                // fault then fails every retry and surfaces once they run out.
                // The caller rolls back before the conflict escapes, which
                // PostgreSQL requires: a failed statement aborts the whole
                // transaction there.
                throw new OrmSagaConcurrencyException(s_sagaTypeKey, correlationKey, ex);
            }

            return;
        }

        _log.LogDebug("Updating saga row {SagaType}/{Key}", s_sagaTypeKey, key);
        var affected = await _repo.UpdateAsync(
            s_sagaTypeKey, correlationKey, newState, newFsmState,
            newRowVersion, expectedRowVersion, now, tx, ct).ConfigureAwait(false);

        if (affected == 0)
        {
            // The row version moved since we read it, so the predicate matched
            // nothing -- someone else wrote first, or removed the row outright.
            // Keep the stale version: it is the only evidence this store is
            // behind. Forgetting it would turn a repeated save into an insert
            // that re-creates a saga another writer ended. A retry reloads,
            // which replaces it.
            throw new OrmSagaConcurrencyException(s_sagaTypeKey, correlationKey);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The remove is checked against what this store observed at load, as
    /// <see cref="SaveAsync"/> is, and as the EF Core and Redis stores do:
    /// </para>
    /// <list type="bullet">
    /// <item>A loaded row is deleted only while its row version is still the
    /// one this store loaded. A row another writer changed or deleted since is
    /// a conflict, so a completing step cannot discard their progress.</item>
    /// <item>A load that found no row must still find none. A row another
    /// writer created since is a conflict and is kept. When there is still no
    /// row, as for a saga one event both starts and completes, nothing is
    /// deleted and the remove succeeds.</item>
    /// <item>With no load at all there is nothing to compare against, and the
    /// delete is unconditional.</item>
    /// </list>
    /// <para>
    /// Like <see cref="SaveAsync"/>, the removal runs in a transaction together with every
    /// registered <see cref="IOrmSagaTransactionContributor"/>, and commits it even when there is
    /// no row to delete.
    /// </para>
    /// </remarks>
    /// <exception cref="OrmSagaConcurrencyException">
    /// Another writer changed, deleted or created the row since this store
    /// loaded it. The generated handler retries, as it does for a save.
    /// </exception>
    public async ValueTask RemoveAsync(TKey key, CancellationToken ct)
    {
        var correlationKey = KeyOf(key);
        var loaded = _loadedVersions.TryGetValue(key, out var expectedRowVersion);

        var openedHere = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            var tx = await _repo.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (tx.ConfigureAwait(false))
            {
                try
                {
                    await RemoveInTransactionAsync(key, correlationKey, loaded, expectedRowVersion, tx, ct)
                        .ConfigureAwait(false);
                    await ContributeAndCommitAsync(tx, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await RollbackAsync(tx, ex).ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            await CloseAsync(openedHere).ConfigureAwait(false);
        }

        // The row is gone as of our own delete, or was absent and still is.
        // Either way that is an observation too.
        _loadedVersions[key] = null;
    }

    private async ValueTask RemoveInTransactionAsync(
        TKey key, string correlationKey, bool loaded, byte[]? expectedRowVersion,
        IAsyncDbTransaction tx, CancellationToken ct)
    {
        if (!loaded)
        {
            var deleted = await _repo.DeleteAsync(s_sagaTypeKey, correlationKey, tx, ct).ConfigureAwait(false);
            _log.LogDebug(
                "Removing {Affected} row(s) for saga {SagaType}/{Key} without a prior load",
                deleted, s_sagaTypeKey, key);
            return;
        }

        if (expectedRowVersion is null)
        {
            // This store saw no row. There is nothing of ours to delete, and a
            // row that exists now belongs to another writer.
            var current = await _repo.GetInTransactionAsync(s_sagaTypeKey, correlationKey, tx, ct).ConfigureAwait(false);
            if (current is not null)
            {
                throw new OrmSagaConcurrencyException(s_sagaTypeKey, correlationKey);
            }

            _log.LogDebug("No saga row to remove for {SagaType}/{Key}", s_sagaTypeKey, key);
            return;
        }

        var affected = await _repo.DeleteVersionedAsync(
            s_sagaTypeKey, correlationKey, expectedRowVersion, tx, ct).ConfigureAwait(false);
        if (affected == 0)
        {
            // Same as a stale update: the row moved or vanished since the load.
            // The stale version stays for the reason given in SaveAsync.
            throw new OrmSagaConcurrencyException(s_sagaTypeKey, correlationKey);
        }

        _log.LogDebug("Removing saga row {SagaType}/{Key}", s_sagaTypeKey, key);
    }

    // The contributors write after the saga statement has passed its
    // concurrency check, so a conflict never reaches them. Then the whole
    // transaction commits at once.
    private async ValueTask ContributeAndCommitAsync(IAsyncDbTransaction tx, CancellationToken ct)
    {
        foreach (var contributor in _contributors)
        {
            await contributor.ContributeAsync(tx, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    // CancellationToken.None: a cancelled operation must still release its
    // transaction. A failed rollback is logged rather than thrown, so the
    // caller sees the exception that caused it, such as the conflict the retry
    // loop acts on. The database discards an uncommitted transaction when the
    // connection closes in any case.
    private async ValueTask RollbackAsync(IAsyncDbTransaction tx, Exception cause)
    {
        try
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackFailure)
        {
            _log.LogWarning(
                rollbackFailure,
                "Rolling back the transaction for saga {SagaType} failed after {Cause}",
                s_sagaTypeKey, cause.GetType().Name);
        }
    }

    // The ORM opens a closed connection for each statement and closes it
    // again. A transaction spans several statements, so the store does the
    // same around the whole transaction instead.
    private async ValueTask<bool> OpenAsync(CancellationToken ct)
    {
        var connection = _repo.Connection;
        if (connection.State == ConnectionState.Open)
        {
            return false;
        }

        await connection.OpenAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async ValueTask CloseAsync(bool openedHere)
    {
        if (openedHere)
        {
            await _repo.Connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static TSaga Rehydrate(SagaInstanceRow row)
    {
        var saga = new TSaga();
        var persistable = (ISagaPersistableState)saga;
        persistable.Restore(row.State);
        persistable.SetFsmStateFromName(row.CurrentFsmState);
        return saga;
    }

    // Matches the EF Core backend's key encoding so both write the same rows.
    private static string KeyOf(TKey key) => key.ToString() ?? string.Empty;

    // Any value that will not repeat works; a GUID needs no coordination and no
    // round-trip, which a database-side sequence or counter would.
    private static byte[] NewRowVersion() => Guid.NewGuid().ToByteArray();
}
