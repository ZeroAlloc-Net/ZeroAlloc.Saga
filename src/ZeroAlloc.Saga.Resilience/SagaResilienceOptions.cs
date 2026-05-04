using ZeroAlloc.Resilience;

namespace ZeroAlloc.Saga.Resilience;

/// <summary>
/// Configures the resilience policies that wrap each call to
/// <see cref="ISagaCommandDispatcher.DispatchAsync{TCommand}"/>. Any subset can be
/// configured; null entries are skipped (no allocation, no overhead).
/// </summary>
/// <remarks>
/// Composition order, outermost first:
/// <c>circuit-breaker → rate-limit → timeout → retry → inner.DispatchAsync</c>.
/// A tripped breaker short-circuits before the rate limiter touches its bucket;
/// rate-limit before timeout so a denied token never starts the timeout clock;
/// timeout before retry so each retry attempt has a per-attempt deadline (when
/// configured via <see cref="RetryPolicy.PerAttemptTimeoutMs"/>).
/// </remarks>
public sealed class SagaResilienceOptions
{
    /// <summary>Optional retry policy. Re-runs the inner dispatch on transient failures.</summary>
    public RetryPolicy? Retry { get; set; }

    /// <summary>Optional timeout policy. Bounds total wall-clock time across all retries.</summary>
    public TimeoutPolicy? Timeout { get; set; }

    /// <summary>Optional circuit breaker. Short-circuits dispatch when the failure threshold trips.</summary>
    public CircuitBreakerPolicy? CircuitBreaker { get; set; }

    /// <summary>Optional rate limiter. Denies dispatch when the token bucket is empty.</summary>
    public RateLimiter? RateLimiter { get; set; }
}
