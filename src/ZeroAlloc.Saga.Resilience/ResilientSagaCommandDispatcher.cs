using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Resilience;

namespace ZeroAlloc.Saga.Resilience;

/// <summary>
/// Decorator over <see cref="ISagaCommandDispatcher"/> that wraps each call to
/// <see cref="DispatchAsync{TCommand}"/> in the configured policies from
/// <see cref="SagaResilienceOptions"/>.
/// </summary>
/// <remarks>
/// Composition order, outermost first:
/// <c>circuit-breaker → rate-limit → timeout → retry → inner.DispatchAsync</c>.
/// Any policy left null in <see cref="SagaResilienceOptions"/> is skipped — the
/// path through that layer is a single conditional branch with no allocation.
/// </remarks>
public sealed class ResilientSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly ISagaCommandDispatcher _inner;
    private readonly SagaResilienceOptions _options;

    /// <param name="inner">The dispatcher being decorated.</param>
    /// <param name="options">Configured resilience policies. Any null entry is skipped.</param>
    public ResilientSagaCommandDispatcher(ISagaCommandDispatcher inner, SagaResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _options = options;
    }

    /// <inheritdoc />
    public async ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        // Layer 1 — circuit breaker fast-reject.
        if (_options.CircuitBreaker is { } breaker && !breaker.CanExecute())
        {
            throw new ResilienceException(
                ResiliencePolicy.CircuitBreaker,
                $"Saga command dispatch short-circuited: circuit breaker is {breaker.State}.");
        }

        // Layer 2 — rate limit token bucket.
        if (_options.RateLimiter is { } limiter && !limiter.TryAcquire())
        {
            throw new ResilienceException(
                ResiliencePolicy.RateLimit,
                "Saga command dispatch denied: rate limiter token bucket empty.");
        }

        // Layer 3 — total-operation timeout. Linked CTS lets the caller's
        // ct still cancel; we add a wall-clock deadline on top.
        CancellationTokenSource? timeoutCts = null;
        var effectiveCt = ct;
        try
        {
            if (_options.Timeout is { } timeout)
            {
                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout.TotalMs);
                effectiveCt = timeoutCts.Token;
            }

            // Layer 4 — retry loop. When no retry policy is configured, the
            // inner is invoked exactly once and any exception bubbles.
            await DispatchWithRetryAsync(cmd, effectiveCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The timeout fired (caller's ct is still alive). Surface as a
            // ResilienceException so consumers can disambiguate from real
            // cancellation.
            throw new ResilienceException(
                ResiliencePolicy.Timeout,
                $"Saga command dispatch exceeded the {_options.Timeout!.TotalMs}ms total-operation timeout.");
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private async ValueTask DispatchWithRetryAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        var retry = _options.Retry;
        var breaker = _options.CircuitBreaker;

        if (retry is null)
        {
            await DispatchOnceAsync(cmd, ct, breaker).ConfigureAwait(false);
            return;
        }

        Exception? lastException = null;
        for (var attempt = 0; attempt < retry.MaxAttempts; attempt++)
        {
            try
            {
                await DispatchSingleAttemptAsync(cmd, ct, retry).ConfigureAwait(false);
                breaker?.OnSuccess();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The filter checks ct (the retry-loop's effective token, which
                // may itself be a linked timeout token), NOT the per-attempt
                // linked token created inside DispatchSingleAttemptAsync. A
                // per-attempt timeout firing leaves ct uncancelled, so the
                // filter is false and the OCE is caught as a transient retry
                // trigger by the catch below — exactly what we want.
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                breaker?.OnFailure(ex);
                if (attempt + 1 >= retry.MaxAttempts) break;
                var backoff = retry.GetBackoffMs(attempt);
                if (backoff > 0) await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
        }

        throw new ResilienceException(
            ResiliencePolicy.Retry,
            $"Saga command dispatch exhausted {retry.MaxAttempts} retry attempts.",
            lastException);
    }

    private async ValueTask DispatchOnceAsync<TCommand>(TCommand cmd, CancellationToken ct, CircuitBreakerPolicy? breaker)
        where TCommand : IRequest<Unit>
    {
        try
        {
            await _inner.DispatchAsync(cmd, ct).ConfigureAwait(false);
            breaker?.OnSuccess();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            breaker?.OnFailure(ex);
            throw;
        }
    }

    private async ValueTask DispatchSingleAttemptAsync<TCommand>(TCommand cmd, CancellationToken ct, RetryPolicy retry)
        where TCommand : IRequest<Unit>
    {
        if (retry.PerAttemptTimeoutMs > 0)
        {
            using var perAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perAttemptCts.CancelAfter(retry.PerAttemptTimeoutMs);
            await _inner.DispatchAsync(cmd, perAttemptCts.Token).ConfigureAwait(false);
        }
        else
        {
            await _inner.DispatchAsync(cmd, ct).ConfigureAwait(false);
        }
    }
}
