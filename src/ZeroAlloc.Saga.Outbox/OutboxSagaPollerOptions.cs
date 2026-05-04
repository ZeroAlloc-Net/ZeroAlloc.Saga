using System;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Polling, batching and retry knobs for <see cref="OutboxSagaCommandPoller"/>.
/// </summary>
public sealed class OutboxSagaPollerOptions
{
    /// <summary>How long the poller sleeps between cycles. Default 2s.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum number of pending entries fetched per cycle. Default 32.</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>Total dispatch attempts before an entry is dead-lettered. Default 5.</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>Delay added to <see cref="DateTimeOffset.UtcNow"/> when scheduling the next retry. Default 10s.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);
}
