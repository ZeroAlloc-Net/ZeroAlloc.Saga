namespace ZeroAlloc.Saga;

/// <summary>
/// Thrown when a saga's notification handler exhausts its OCC retry budget
/// after repeated <c>DbUpdateConcurrencyException</c>s (or other backend-level
/// concurrency failures). Indicates persistent contention on the same saga
/// instance — typically more concurrent producers than the backend can serialize
/// within the configured retry window.
/// </summary>
/// <remarks>
/// Tuning options: increase <c>MaxRetryAttempts</c> or backoff window on the
/// durable backend's options object, or eliminate cross-process contention by
/// partitioning event production. The Saga.Outbox bridge (Phase 3) makes
/// command dispatch transactional with state writes, sidestepping the at-most-once
/// dispatch concern that motivates retries.
/// </remarks>
public sealed class SagaConcurrencyException : System.Exception
{
    public string SagaType { get; }
    public string CorrelationKey { get; }
    public int Attempts { get; }

    public SagaConcurrencyException()
    {
        SagaType = string.Empty;
        CorrelationKey = string.Empty;
    }

    public SagaConcurrencyException(string message)
        : base(message)
    {
        SagaType = string.Empty;
        CorrelationKey = string.Empty;
    }

    public SagaConcurrencyException(string message, System.Exception innerException)
        : base(message, innerException)
    {
        SagaType = string.Empty;
        CorrelationKey = string.Empty;
    }

    public SagaConcurrencyException(string sagaType, string correlationKey, int attempts, System.Exception? inner)
        : base($"Saga '{sagaType}' for correlation key '{correlationKey}' failed after " +
               $"{attempts} OCC retry attempts. Another process may be modifying the same " +
               $"saga state at high frequency, or the retry settings need adjustment.", inner)
    {
        SagaType = sagaType;
        CorrelationKey = correlationKey;
        Attempts = attempts;
    }
}
