using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox.Orm.Tests.Fixtures;

/// <summary>
/// Counts the <see cref="MessageDispatchedEvent"/>s that ZeroAlloc.Outbox's worker publishes.
/// The worker publishes that event only after <c>MarkSucceededAsync</c> has returned, so a test
/// that waits for the count before stopping the host never cancels a mark in flight. The ledger
/// is not a safe signal for that: a handler records the command inside <c>DispatchAsync</c>,
/// before the worker marks the row.
/// </summary>
public sealed class DispatchedEventCounter : IOutboxDashboardEventPublisher
{
    private int _dispatched;

    public int Dispatched => Volatile.Read(ref _dispatched);

    public ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct)
    {
        if (evt is MessageDispatchedEvent)
            Interlocked.Increment(ref _dispatched);
        return default;
    }

    // The worker only publishes. A subscription is built by ZeroAlloc.Outbox's own publisher,
    // whose constructor is internal, so this test double cannot hand one out.
    public OutboxDashboardSubscription Subscribe()
        => throw new NotSupportedException("DispatchedEventCounter only counts published events.");
}
