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
    private readonly ILogger _log;

    // Row versions as they were when this store last read or wrote each saga.
    // This is the store's unit of work, and it is what makes the concurrency
    // check meaningful: the predicate has to carry the version observed at
    // LOAD time, not one re-read at save time. Re-reading would compare the
    // row against itself and guard only the instant inside SaveAsync, letting
    // two writers that both loaded and then saved overwrite each other -- the
    // exact lost update the row version exists to prevent.
    //
    // The store is scoped, so this dictionary lives as long as the request
    // that owns it. That is the same unit-of-work boundary EF Core's change
    // tracker gives the EF backend, and like a DbContext it is not safe to
    // share a single instance across concurrent work.
    private readonly Dictionary<TKey, byte[]> _loadedVersions = new();

    /// <summary>
    /// Creates a store over the supplied repository.
    /// </summary>
    /// <param name="repo">Repository bound to the application's connection.</param>
    /// <param name="log">Optional logger.</param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TSaga"/> does not implement <c>ISagaPersistableState</c>.
    /// </exception>
    internal OrmSagaStore(SagaInstanceRepository repo, ILogger<OrmSagaStore<TSaga, TKey>>? log = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));

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
            // Absent now means "insert on save". Drop any version from an
            // earlier read so a row deleted behind our back is not still
            // treated as an update.
            _loadedVersions.Remove(key);
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

        // Decide insert vs update from what this store has seen, not from a
        // fresh read. See _loadedVersions for why re-reading here would defeat
        // the concurrency check entirely.
        if (!_loadedVersions.TryGetValue(key, out var expectedRowVersion))
        {
            var rowVersion = NewRowVersion();
            _log.LogDebug("Inserting new saga row {SagaType}/{Key}", s_sagaTypeKey, key);
            try
            {
                await _repo.InsertAsync(
                    s_sagaTypeKey, correlationKey, newState, newFsmState,
                    rowVersion, now, now, ct).ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                // Losing the insert race means another writer created this
                // instance first. That is a conflict, not a failure: reloading
                // and retrying will find their row. Any other provider error is
                // a genuine fault and propagates.
                throw new OrmSagaConcurrencyException(s_sagaTypeKey, correlationKey, ex);
            }

            _loadedVersions[key] = rowVersion;
            return;
        }

        var newRowVersion = NewRowVersion();
        _log.LogDebug("Updating saga row {SagaType}/{Key}", s_sagaTypeKey, key);
        var affected = await _repo.UpdateAsync(
            s_sagaTypeKey, correlationKey, newState, newFsmState,
            newRowVersion, expectedRowVersion, now, ct).ConfigureAwait(false);

        if (affected == 0)
        {
            // The row version moved since we read it, so the predicate matched
            // nothing -- someone else wrote first, or removed the row outright.
            // Forget our stale version so a retry re-reads rather than
            // repeating the same doomed update.
            _loadedVersions.Remove(key);
            throw new OrmSagaConcurrencyException(s_sagaTypeKey, correlationKey);
        }

        // Our write is now the one others must match against.
        _loadedVersions[key] = newRowVersion;
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(TKey key, CancellationToken ct)
    {
        var affected = await _repo.DeleteAsync(s_sagaTypeKey, KeyOf(key), ct).ConfigureAwait(false);
        _loadedVersions.Remove(key);
        _log.LogDebug(
            "Removed {Affected} row(s) for saga {SagaType}/{Key}", affected, s_sagaTypeKey, key);
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
