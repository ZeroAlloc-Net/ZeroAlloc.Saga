using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// Per-scope buffer for outbox-row writes that need to commit atomically with the
/// next <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/>.
/// </summary>
/// <remarks>
/// Implements <see cref="ISagaUnitOfWork"/> so <c>OutboxSagaCommandDispatcher</c>
/// enlists writes here. The buffered entries are drained by
/// <see cref="RedisOutboxTransactionContributor"/> inside the saga store's
/// <c>MULTI/EXEC</c> via the <c>IRedisSagaTransactionContributor</c> hook, so a
/// failed save discards the writes by virtue of <c>EXEC</c> aborting (saga update +
/// outbox row roll back together — exactly Phase 3a's atomicity contract, now
/// extended to Redis).
/// </remarks>
public sealed class RedisSagaUnitOfWork : ISagaUnitOfWork
{
    private readonly List<PendingWrite> _pending = new();
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    /// <inheritdoc />
    public ValueTask EnlistOutboxRowAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var entry = new PendingWrite(OutboxMessageId.New(), typeName, payload.ToArray(), DateTimeOffset.UtcNow);
        lock (_gate) _pending.Add(entry);
        return ValueTask.CompletedTask;
    }

    /// <summary>Drains the buffer atomically; returns the snapshot. Reset for the next save.</summary>
    internal IReadOnlyList<PendingWrite> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0) return System.Array.Empty<PendingWrite>();
            var snapshot = _pending.ToArray();
            _pending.Clear();
            return snapshot;
        }
    }

    /// <summary>An enlisted outbox-row write awaiting the saga store's MULTI.</summary>
    internal readonly record struct PendingWrite(OutboxMessageId Id, string TypeName, byte[] Payload, DateTimeOffset CreatedAt);
}
