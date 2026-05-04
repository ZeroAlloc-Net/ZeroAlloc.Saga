using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ZeroAlloc.Saga.Redis;

/// <summary>
/// Redis-backed <see cref="ISagaStore{TSaga,TKey}"/>. Stores each saga as a Redis Hash with
/// <c>state</c> (bytes via <see cref="ISagaPersistableState.Snapshot"/>) and <c>version</c>
/// (Guid string, rotated on every save) fields. OCC is enforced via
/// <c>WATCH</c> + <c>HGET version</c> + <c>MULTI/EXEC</c>; a conflict raises
/// <see cref="RedisSagaConcurrencyException"/> which the generator-emitted retry loop
/// catches alongside EfCore's <c>DbUpdateConcurrencyException</c>.
/// </summary>
/// <remarks>
/// Per-instance <c>_observedVersions</c> dictionary tracks the version observed at the
/// most recent <see cref="LoadOrCreateAsync"/> per correlation key, so the eventual
/// <see cref="SaveAsync"/> can verify nothing else has touched the key. Cleared on
/// successful save / remove. The store is registered as Scoped — the scope-per-attempt
/// retry loop in the generator-emitted handler creates a fresh store (and fresh
/// observed-version map) per attempt, mirroring the EfCore backend's per-scope semantics.
/// </remarks>
public sealed class RedisSagaStore<TSaga, TKey> : ISagaStore<TSaga, TKey>
    where TSaga : class, new()
    where TKey : notnull, System.IEquatable<TKey>
{
    private readonly IDatabase _db;
    private readonly RedisSagaStoreOptions _options;
    private readonly ILogger<RedisSagaStore<TSaga, TKey>> _log;

    /// <summary>
    /// Per-key version remembered between LoadOrCreateAsync and the next SaveAsync.
    /// A null value means "the key did not exist when loaded" — the next save is an
    /// INSERT (uses an EXISTS-check inside the WATCH/MULTI to detect a concurrent INSERT).
    /// </summary>
    private readonly ConcurrentDictionary<string, string?> _observedVersions = new(StringComparer.Ordinal);

    public RedisSagaStore(IDatabase db, RedisSagaStoreOptions options, ILogger<RedisSagaStore<TSaga, TKey>> log)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _db = db;
        _options = options;
        _log = log;
    }

    /// <inheritdoc />
    public async ValueTask<TSaga?> TryLoadAsync(TKey key, CancellationToken ct)
    {
        var redisKey = BuildKey(key);
        var values = await _db.HashGetAsync(redisKey, [(RedisValue)"state", (RedisValue)"version"]).ConfigureAwait(false);
        var stateValue = values[0];
        var versionValue = values[1];

        if (stateValue.IsNull || versionValue.IsNull)
        {
            _observedVersions[redisKey] = null;
            return null;
        }

        _observedVersions[redisKey] = (string?)versionValue;
        return Deserialize((byte[])stateValue!);
    }

    /// <inheritdoc />
    public async ValueTask<TSaga> LoadOrCreateAsync(TKey key, CancellationToken ct)
    {
        var existing = await TryLoadAsync(key, ct).ConfigureAwait(false);
        return existing ?? new TSaga();
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(TKey key, TSaga saga, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(saga);
        var redisKey = BuildKey(key);
        var observedVersion = _observedVersions.TryGetValue(redisKey, out var v) ? v : null;
        var newVersion = Guid.NewGuid().ToString("N");
        var stateBytes = Serialize(saga);

        await _db.ExecuteAsync("WATCH", (RedisKey)redisKey).ConfigureAwait(false);
        try
        {
            var currentVersion = (string?)await _db.HashGetAsync(redisKey, "version").ConfigureAwait(false);
            if (!string.Equals(currentVersion, observedVersion, StringComparison.Ordinal))
            {
                _log.LogDebug("RedisSagaStore: version mismatch on {Key}: expected={Expected}, actual={Actual}",
                    redisKey, observedVersion ?? "<none>", currentVersion ?? "<none>");
                throw new RedisSagaConcurrencyException(redisKey);
            }

            var tran = _db.CreateTransaction();
            _ = tran.HashSetAsync(redisKey, [
                new HashEntry("state", stateBytes),
                new HashEntry("version", newVersion),
            ]);
            var committed = await tran.ExecuteAsync().ConfigureAwait(false);
            if (!committed)
            {
                throw new RedisSagaConcurrencyException(redisKey);
            }
        }
        finally
        {
            // ExecuteAsync auto-unwatches on commit/abort; this UNWATCH covers the
            // version-mismatch throw path above.
            await _db.ExecuteAsync("UNWATCH").ConfigureAwait(false);
        }

        _observedVersions[redisKey] = newVersion;
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(TKey key, CancellationToken ct)
    {
        var redisKey = BuildKey(key);
        var observedVersion = _observedVersions.TryGetValue(redisKey, out var v) ? v : null;

        await _db.ExecuteAsync("WATCH", (RedisKey)redisKey).ConfigureAwait(false);
        try
        {
            var currentVersion = (string?)await _db.HashGetAsync(redisKey, "version").ConfigureAwait(false);
            // For Remove, accept "key already gone" as a no-op rather than raising;
            // the saga handler treats post-Complete idempotently. Only raise on a
            // genuine "someone changed it" mismatch.
            if (currentVersion is not null && observedVersion is not null
                && !string.Equals(currentVersion, observedVersion, StringComparison.Ordinal))
            {
                throw new RedisSagaConcurrencyException(redisKey);
            }

            var tran = _db.CreateTransaction();
            _ = tran.KeyDeleteAsync(redisKey);
            var committed = await tran.ExecuteAsync().ConfigureAwait(false);
            if (!committed)
            {
                throw new RedisSagaConcurrencyException(redisKey);
            }
        }
        finally
        {
            await _db.ExecuteAsync("UNWATCH").ConfigureAwait(false);
        }

        _observedVersions.TryRemove(redisKey, out _);
    }

    private string BuildKey(TKey key)
        => $"{_options.KeyPrefix}:{typeof(TSaga).Name}:{key}";

    private static byte[] Serialize(TSaga saga)
    {
        if (saga is not ISagaPersistableState p)
        {
            throw new InvalidOperationException(
                $"RedisSagaStore: {typeof(TSaga).FullName} does not implement ISagaPersistableState. " +
                "The saga generator should have made this implementation a partial — make sure the [Saga] attribute is applied and the type is partial.");
        }
        return p.Snapshot();
    }

    private static TSaga Deserialize(byte[] bytes)
    {
        var saga = new TSaga();
        if (saga is not ISagaPersistableState p)
        {
            throw new InvalidOperationException(
                $"RedisSagaStore: {typeof(TSaga).FullName} does not implement ISagaPersistableState. See Snapshot() for details.");
        }
        p.Restore(bytes);
        return saga;
    }
}
