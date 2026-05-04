using System;
using StackExchange.Redis;
using ZeroAlloc.Saga.Redis;

namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// Bridges <see cref="RedisSagaUnitOfWork"/> into the Redis saga store's MULTI/EXEC
/// via the <see cref="IRedisSagaTransactionContributor"/> hook. On every saga
/// <c>SaveAsync</c>, drains the unit-of-work's buffered outbox-row writes and queues
/// the corresponding <c>HSET</c> + <c>ZADD</c> commands on the transaction so they
/// commit atomically with the saga state save.
/// </summary>
public sealed class RedisOutboxTransactionContributor : IRedisSagaTransactionContributor
{
    private readonly RedisSagaUnitOfWork _uow;
    private readonly RedisOutboxOptions _options;

    public RedisOutboxTransactionContributor(RedisSagaUnitOfWork uow, RedisOutboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(options);
        _uow = uow;
        _options = options;
    }

    /// <inheritdoc />
    public void Contribute(ITransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var pending = _uow.Drain();
        if (pending.Count == 0) return;

        var pendingKey = $"{_options.KeyPrefix}:pending";
        foreach (var w in pending)
        {
            var entryKey = $"{_options.KeyPrefix}:entry:{w.Id}";
            // Hash fields match the RedisOutboxStore reader.
            _ = transaction.HashSetAsync(entryKey, [
                new HashEntry("typeName", w.TypeName),
                new HashEntry("payload", w.Payload),
                new HashEntry("retryCount", 0),
                new HashEntry("status", "Pending"),
                new HashEntry("createdAt", w.CreatedAt.ToUnixTimeMilliseconds()),
            ]);
            // Sorted set with score = next-retry tick (initially createdAt) so
            // FetchPendingAsync finds entries via ZRANGEBYSCORE ≤ now.
            _ = transaction.SortedSetAddAsync(pendingKey, w.Id.ToString(), w.CreatedAt.ToUnixTimeMilliseconds());
        }
    }
}
