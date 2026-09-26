using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace AotSmokeOutbox;

/// <summary>
/// Tiny in-process <see cref="IOutboxStore"/> on the ZeroAlloc.Outbox 3.0 lease contract, for the
/// AOT smoke. Auto-commits each enqueue; the deferred-EfCore semantics don't matter here. The
/// load-bearing AOT contract is that the saga generator's MediatorSagaCommandDispatcher roots
/// SagaCommandRegistry, so WithOutbox()'s reflective lookup works after trimming. One lock guards
/// everything, which is all a single-process smoke needs.
/// </summary>
internal sealed class InProcessOutboxStore : IOutboxStore
{
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = new();

    public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
            _entries.Add(new Entry(OutboxMessageId.New(), typeName, payload.ToArray(), now));
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var claimed = new List<OutboxEntry>();
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (claimed.Count >= batchSize) break;
                if (e.State != EntryState.Pending || e.NextRetryAt > now) continue;
                if (e.LockedUntil is { } until && until >= now) continue;
                e.LockedBy = lease.HostId;
                e.LockedUntil = now + lease.Duration;
                claimed.Add(new OutboxEntry
                {
                    Id = e.Id,
                    TypeName = e.TypeName,
                    RawPayload = e.Payload,
                    RetryCount = e.RetryCount,
                    CreatedAt = e.CreatedAt,
                });
            }
        }
        return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(claimed);
    }

    public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var e = Find(id);
            if (e is null || !IsHeldBy(e, lease) || e.LockedUntil is not { } until || until < now)
                return ValueTask.FromResult(false);
            e.LockedUntil = now + lease.Duration;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<int> ReleaseLeasesAsync(IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
    {
        var released = 0;
        lock (_gate)
        {
            foreach (var id in ids)
            {
                var e = Find(id);
                if (e is null || !IsHeldBy(e, lease)) continue;
                e.LockedBy = null;
                e.LockedUntil = null;
                released++;
            }
        }
        return ValueTask.FromResult(released);
    }

    public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
        => Mark(id, lease, e => e.State = EntryState.Succeeded);

    public ValueTask<bool> MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
        => Mark(id, lease, e =>
        {
            e.RetryCount = retryCount;
            e.NextRetryAt = nextRetryAt;
        });

    public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
        => Mark(id, lease, e => e.State = EntryState.DeadLetter);

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public int SucceededCount
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var e in _entries) if (e.State == EntryState.Succeeded) n++;
                return n;
            }
        }
    }

    // A mark applies only while this host holds the lease on a pending entry, and clears the lease.
    private ValueTask<bool> Mark(OutboxMessageId id, OutboxLease lease, Action<Entry> apply)
    {
        lock (_gate)
        {
            var e = Find(id);
            if (e is null || !IsHeldBy(e, lease)) return ValueTask.FromResult(false);
            apply(e);
            e.LockedBy = null;
            e.LockedUntil = null;
            return ValueTask.FromResult(true);
        }
    }

    private static bool IsHeldBy(Entry e, OutboxLease lease)
        => e.State == EntryState.Pending && string.Equals(e.LockedBy, lease.HostId, StringComparison.Ordinal);

    // Callers hold _gate.
    private Entry? Find(OutboxMessageId id)
    {
        foreach (var e in _entries)
            if (e.Id == id) return e;
        return null;
    }

    private enum EntryState { Pending, Succeeded, DeadLetter }

    private sealed class Entry
    {
        public Entry(OutboxMessageId id, string typeName, byte[] payload, DateTimeOffset now)
        { Id = id; TypeName = typeName; Payload = payload; CreatedAt = now; NextRetryAt = now; }
        public OutboxMessageId Id { get; }
        public string TypeName { get; }
        public byte[] Payload { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset NextRetryAt { get; set; }
        public int RetryCount { get; set; }
        public EntryState State { get; set; }
        public string? LockedBy { get; set; }
        public DateTimeOffset? LockedUntil { get; set; }
    }
}
