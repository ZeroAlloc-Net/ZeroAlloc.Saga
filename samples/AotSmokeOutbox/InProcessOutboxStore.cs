using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace AotSmokeOutbox;

/// <summary>
/// Tiny in-process <see cref="IOutboxStore"/> for the AOT smoke. Auto-commits
/// each enqueue (we don't need the deferred-EfCore semantics here — the load-bearing
/// AOT contract is that the saga generator's MediatorSagaCommandDispatcher roots
/// SagaCommandRegistry so the poller's reflective lookup works after trimming).
/// </summary>
internal sealed class InProcessOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<OutboxMessageId, Entry> _entries = new();

    public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
    {
        var entry = new Entry(OutboxMessageId.New(), typeName, payload.ToArray());
        _entries[entry.Id] = entry;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
    {
        var results = new List<OutboxEntry>();
        foreach (var kv in _entries)
        {
            if (!kv.Value.Succeeded)
            {
                results.Add(new OutboxEntry
                {
                    Id = kv.Value.Id,
                    TypeName = kv.Value.TypeName,
                    RawPayload = kv.Value.Payload,
                    RetryCount = kv.Value.RetryCount,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                if (results.Count >= batchSize) break;
            }
        }
        return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(results);
    }

    public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry)) entry.Succeeded = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
    {
        if (_entries.TryGetValue(id, out var entry)) entry.RetryCount = retryCount;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
    {
        _entries.TryRemove(id, out _);
        return ValueTask.CompletedTask;
    }

    public int Count => _entries.Count;
    public int SucceededCount
    {
        get
        {
            var n = 0;
            foreach (var kv in _entries) if (kv.Value.Succeeded) n++;
            return n;
        }
    }

    private sealed class Entry
    {
        public Entry(OutboxMessageId id, string typeName, byte[] payload)
        { Id = id; TypeName = typeName; Payload = payload; }
        public OutboxMessageId Id { get; }
        public string TypeName { get; }
        public byte[] Payload { get; }
        public int RetryCount { get; set; }
        public bool Succeeded { get; set; }
    }
}
