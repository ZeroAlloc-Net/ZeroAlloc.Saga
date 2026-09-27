using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// Redis-backed <see cref="IOutboxStore"/> on the ZeroAlloc.Outbox 3.0 lease contract. Stores each
/// entry as a Hash (<c>{KeyPrefix}:entry:{id}</c>) and tracks pending ids in the sorted set
/// <c>{KeyPrefix}:pending</c>.
/// </summary>
/// <remarks>
/// <para>
/// ZeroAlloc.Outbox's <c>OutboxWorkerService</c> drives it: it claims a batch under a lease, renews
/// each entry's lease right before dispatching it, records the outcome, and releases what it did
/// not finish. Each of those operations is one Lua script, run atomically on the server, so two
/// hosts never claim the same entry and a mark applies only while its host holds the lease.
/// </para>
/// <para>
/// A pending entry's score is its lease expiry while it is leased, and the time it is next due
/// otherwise. A claim therefore sees only due, unleased entries, and an entry whose holder died
/// becomes claimable again when its lease runs out.
/// </para>
/// <para>
/// The atomicity-with-saga-save story is owned by <see cref="RedisOutboxTransactionContributor"/>.
/// <see cref="EnqueueDeferredAsync"/> throws, because <see cref="RedisSagaUnitOfWork"/> is the
/// supported write path. <see cref="EnqueueAsync"/> persists an entry directly, for the rare
/// consumer using this store outside the saga bridge, with no transactional grouping.
/// </para>
/// <para>
/// Times come from this host's clock, as in the other ZeroAlloc.Outbox stores, so hosts need
/// roughly synchronised clocks. Redis Cluster is not supported: the claim script accesses entry
/// hashes it derives from the key prefix instead of declaring each one in <c>KEYS</c>, and the
/// saga store's MULTI/EXEC spans saga and outbox keys. A key-prefixed <see cref="IDatabase"/>
/// works, because every key a script touches is derived from <c>KEYS</c>.
/// </para>
/// </remarks>
public sealed class RedisOutboxStore : IOutboxStore
{
    /// <summary>Values the claim script returns per entry: id, typeName, payload, retryCount, createdAt.</summary>
    private const int ClaimedValuesPerEntry = 5;

    private readonly IDatabase _db;
    private readonly string _entryKeyPrefix;
    private readonly RedisKey _pendingKey;
    private readonly RedisKey _succeededKey;
    private readonly RedisKey _deadLetterKey;

    public RedisOutboxStore(IDatabase db, RedisOutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _entryKeyPrefix = $"{options.KeyPrefix}:entry:";
        _pendingKey = $"{options.KeyPrefix}:pending";
        _succeededKey = $"{options.KeyPrefix}:succeeded";
        _deadLetterKey = $"{options.KeyPrefix}:deadletter";
    }

    /// <inheritdoc />
    public async ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
    {
        if (transaction is not null)
        {
            // The IOutboxStore contract carries a DbTransaction for relational backends;
            // Redis is not relational and a passed-in DbTransaction is almost certainly a
            // misconfiguration (e.g. the caller thinks they're using the EfCore backend).
            // Throw rather than silently dropping the transaction — atomic semantics
            // wouldn't be honoured anyway and silent drops mask real bugs.
            throw new InvalidOperationException(
                "RedisOutboxStore.EnqueueAsync received a non-null DbTransaction. Redis does not " +
                "participate in ADO.NET transactions; if the caller expects transactional grouping, " +
                "wire WithRedisOutbox() and use RedisSagaUnitOfWork (which joins the saga store's " +
                "MULTI/EXEC). Otherwise pass null for the transaction parameter.");
        }
        var id = OutboxMessageId.New().ToString();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // No lease fields: absent means unleased.
        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(EntryKey(id), [
            new HashEntry("typeName", typeName),
            new HashEntry("payload", payload.ToArray()),
            new HashEntry("retryCount", 0),
            new HashEntry("status", "Pending"),
            new HashEntry("createdAt", now),
        ]);
        _ = tran.SortedSetAddAsync(_pendingKey, id, now);
        await tran.ExecuteAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask EnqueueDeferredAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        // The Redis bridge's atomic-dispatch path goes through RedisSagaUnitOfWork +
        // RedisOutboxTransactionContributor, NOT through IOutboxStore.EnqueueDeferredAsync.
        // OutboxSagaCommandDispatcher resolves ISagaUnitOfWork (registered as
        // RedisSagaUnitOfWork by WithRedisOutbox()), so this overload should not be
        // reached on the dispatch path. Throw loudly if a custom configuration ends up
        // here so misconfiguration doesn't silently lose atomicity.
        throw new InvalidOperationException(
            "RedisOutboxStore.EnqueueDeferredAsync should not be called directly when WithRedisOutbox() is configured. " +
            "The dispatch path uses RedisSagaUnitOfWork to enlist outbox-row writes for atomic commit with the saga store's MULTI/EXEC. " +
            "If you reached this method via OutboxSagaCommandDispatcher, verify that WithRedisOutbox() is registered AFTER WithOutbox().");
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ThrowIfNoHost(lease);
        ThrowIfShorterThanOneMillisecond(lease);

        var now = DateTimeOffset.UtcNow;
        var result = await _db.ScriptEvaluateAsync(
            RedisOutboxScripts.Claim,
            [_pendingKey, (RedisKey)_entryKeyPrefix],
            [
                now.ToUnixTimeMilliseconds(),
                batchSize,
                lease.HostId,
                (now + lease.Duration).ToUnixTimeMilliseconds(),
            ]).ConfigureAwait(false);

        var values = (RedisResult[]?)result;
        if (values is null || values.Length == 0)
            return Array.Empty<OutboxEntry>();

        var entries = new List<OutboxEntry>(values.Length / ClaimedValuesPerEntry);
        for (var i = 0; i + ClaimedValuesPerEntry <= values.Length; i += ClaimedValuesPerEntry)
        {
            // An id that does not parse was not written by this store. It stays leased, and is
            // offered again when the lease runs out; nothing can mark it without a parsed id.
            if (!OutboxMessageId.TryParse((string?)values[i], null, out var id))
                continue;

            // A pending entry without a type name or payload is returned with empty values, so
            // the worker dead-letters it for having no dispatcher rather than it disappearing.
            entries.Add(new OutboxEntry
            {
                Id = id,
                TypeName = (string?)values[i + 1] ?? string.Empty,
                RawPayload = (byte[]?)values[i + 2] ?? [],
                RetryCount = (int)(long)values[i + 3],
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)values[i + 4]),
            });
        }
        return entries;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);
        ThrowIfShorterThanOneMillisecond(lease);

        var now = DateTimeOffset.UtcNow;
        var idText = id.ToString();
        var result = await _db.ScriptEvaluateAsync(
            RedisOutboxScripts.Renew,
            [EntryKey(idText), _pendingKey],
            [idText, lease.HostId, now.ToUnixTimeMilliseconds(), (now + lease.Duration).ToUnixTimeMilliseconds()])
            .ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ThrowIfNoHost(lease);
        if (ids.Count == 0)
            return 0;

        // Entry hashes go in KEYS, so a key-prefixed IDatabase prefixes them; the ids go in ARGV
        // as the pending-set members, with ARGV[i + 1] belonging to KEYS[i].
        var keys = new RedisKey[ids.Count + 1];
        var values = new RedisValue[ids.Count + 2];
        keys[0] = _pendingKey;
        values[0] = lease.HostId;
        values[1] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < ids.Count; i++)
        {
            var idText = ids[i].ToString();
            keys[i + 1] = EntryKey(idText);
            values[i + 2] = idText;
        }

        var result = await _db.ScriptEvaluateAsync(RedisOutboxScripts.Release, keys, values)
            .ConfigureAwait(false);
        return (int)(long)result;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var idText = id.ToString();
        var result = await _db.ScriptEvaluateAsync(
            RedisOutboxScripts.MarkSucceeded,
            [EntryKey(idText), _pendingKey, _succeededKey],
            [idText, lease.HostId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()]).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> MarkFailedAsync(
        OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var idText = id.ToString();
        var result = await _db.ScriptEvaluateAsync(
            RedisOutboxScripts.MarkFailed,
            [EntryKey(idText), _pendingKey],
            [idText, lease.HostId, retryCount, nextRetryAt.ToUnixTimeMilliseconds()]).ConfigureAwait(false);
        return (long)result == 1;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
    {
        ThrowIfNoHost(lease);

        var idText = id.ToString();
        var result = await _db.ScriptEvaluateAsync(
            RedisOutboxScripts.DeadLetter,
            [EntryKey(idText), _pendingKey, _deadLetterKey],
            [idText, lease.HostId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), error ?? string.Empty])
            .ConfigureAwait(false);
        return (long)result == 1;
    }

    private RedisKey EntryKey(string id) => _entryKeyPrefix + id;

    // A blank host id identifies no host, and the scripts would treat it as a lease holder.
    private static void ThrowIfNoHost(OutboxLease lease)
        => ArgumentException.ThrowIfNullOrWhiteSpace(lease.HostId, nameof(lease));

    // Lease times are whole unix milliseconds. A shorter lease would truncate to lockedUntil ==
    // now, and a second claim in the same millisecond could take the entry.
    private static void ThrowIfShorterThanOneMillisecond(OutboxLease lease)
    {
        if (lease.Duration < TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lease), lease.Duration, "OutboxLease.Duration must be at least 1 millisecond.");
        }
    }
}
