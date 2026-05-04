using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// Redis-backed <see cref="IOutboxStore"/>. Stores each entry as a Hash
/// (<c>{KeyPrefix}:entry:{id}</c>) and tracks pending IDs in a sorted set
/// (<c>{KeyPrefix}:pending</c>) keyed by the timestamp the poller should retry at.
/// </summary>
/// <remarks>
/// Used by <see cref="OutboxSagaCommandPoller"/> to drain enqueued saga commands.
/// The atomicity-with-saga-save story is owned by <see cref="RedisOutboxTransactionContributor"/>;
/// this class only implements the read/mark/dead-letter side of the contract.
/// <para>
/// The deferred-write override (<see cref="EnqueueDeferredAsync"/>) is intentionally a
/// no-op that throws — direct enqueue under <c>WithRedisStore</c> + <c>WithOutbox</c> is
/// not the supported path; <see cref="RedisSagaUnitOfWork"/> is the canonical entry point.
/// <see cref="EnqueueAsync"/> is supported (for the rare consumer using <c>RedisOutboxStore</c>
/// outside the saga bridge) and persists the entry directly with no transactional grouping.
/// </para>
/// </remarks>
public sealed class RedisOutboxStore : IOutboxStore
{
    private readonly IDatabase _db;
    private readonly RedisOutboxOptions _options;

    public RedisOutboxStore(IDatabase db, RedisOutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        _db = db;
        _options = options;
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
        var id = OutboxMessageId.New();
        var entryKey = $"{_options.KeyPrefix}:entry:{id}";
        var pendingKey = $"{_options.KeyPrefix}:pending";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(entryKey, [
            new HashEntry("typeName", typeName),
            new HashEntry("payload", payload.ToArray()),
            new HashEntry("retryCount", 0),
            new HashEntry("status", "Pending"),
            new HashEntry("createdAt", now),
        ]);
        _ = tran.SortedSetAddAsync(pendingKey, id.ToString(), now);
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
    public async ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
    {
        var pendingKey = $"{_options.KeyPrefix}:pending";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ids = await _db.SortedSetRangeByScoreAsync(pendingKey, stop: now, take: batchSize).ConfigureAwait(false);

        var results = new List<OutboxEntry>(ids.Length);
        foreach (var idValue in ids)
        {
            var idStr = (string?)idValue;
            if (idStr is null) continue;
            // If the sorted-set member can't be parsed back to an OutboxMessageId, the
            // poller can't safely Mark*/DeadLetter it (the round-trip key wouldn't match).
            // Skip and leave the entry in pending — operator intervention is preferable
            // to fabricating a fresh id that diverges from what's in Redis.
            if (!OutboxMessageId.TryParse(idStr, null, out var id)) continue;
            var entryKey = $"{_options.KeyPrefix}:entry:{idStr}";
            var fields = await _db.HashGetAsync(entryKey,
                [(RedisValue)"typeName", (RedisValue)"payload", (RedisValue)"retryCount", (RedisValue)"createdAt"])
                .ConfigureAwait(false);

            var typeName = (string?)fields[0];
            var payload = (byte[]?)fields[1];
            if (typeName is null || payload is null) continue;

            results.Add(new OutboxEntry
            {
                Id = id,
                TypeName = typeName,
                RawPayload = payload,
                RetryCount = (int)(fields[2].IsNull ? 0 : (long)fields[2]),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(fields[3].IsNull ? 0 : (long)fields[3]),
            });
        }
        return results;
    }

    /// <inheritdoc />
    public async ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
    {
        var entryKey = $"{_options.KeyPrefix}:entry:{id}";
        var pendingKey = $"{_options.KeyPrefix}:pending";
        var succeededKey = $"{_options.KeyPrefix}:succeeded";

        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(entryKey, [
            new HashEntry("status", "Succeeded"),
            new HashEntry("processedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ]);
        _ = tran.SortedSetRemoveAsync(pendingKey, id.ToString());
        _ = tran.SetAddAsync(succeededKey, id.ToString());
        await tran.ExecuteAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        var entryKey = $"{_options.KeyPrefix}:entry:{id}";
        var pendingKey = $"{_options.KeyPrefix}:pending";
        var score = nextRetryAt.ToUnixTimeMilliseconds();

        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(entryKey, [
            new HashEntry("retryCount", retryCount),
            new HashEntry("status", "Failed"),
            new HashEntry("nextRetryAt", score),
        ]);
        // Re-add to pending sorted set with the new (later) score so the poller picks
        // it up again at nextRetryAt — ZADD overwrites the existing score for the member.
        _ = tran.SortedSetAddAsync(pendingKey, id.ToString(), score);
        await tran.ExecuteAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
    {
        var entryKey = $"{_options.KeyPrefix}:entry:{id}";
        var pendingKey = $"{_options.KeyPrefix}:pending";
        var deadKey = $"{_options.KeyPrefix}:deadletter";

        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(entryKey, [
            new HashEntry("status", "DeadLetter"),
            new HashEntry("error", error ?? string.Empty),
            new HashEntry("processedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ]);
        _ = tran.SortedSetRemoveAsync(pendingKey, id.ToString());
        _ = tran.SetAddAsync(deadKey, id.ToString());
        await tran.ExecuteAsync().ConfigureAwait(false);
    }

}
