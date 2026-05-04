using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;
using ZeroAlloc.Resilience;
using ZeroAlloc.Saga.Resilience;

namespace ZeroAlloc.Saga.Resilience.Tests;

public class ResilientSagaCommandDispatcherTests
{
    public readonly record struct Cmd(int X) : IRequest<Unit>;

    // Needed for ZAM001 satisfaction — generator emits IMediator.Send overloads
    // for each IRequest<T> in the project; every one needs a registered handler.
    public sealed class CmdHandler : IRequestHandler<Cmd, Unit>
    {
        public ValueTask<Unit> Handle(Cmd request, CancellationToken cancellationToken) => new(Unit.Value);
    }

    private sealed class CountingDispatcher : ISagaCommandDispatcher
    {
        public int CallCount;
        private readonly Func<int, CancellationToken, Task>? _onCall;
        public CountingDispatcher(Func<int, CancellationToken, Task>? onCall = null) => _onCall = onCall;

        public async ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
            where TCommand : IRequest<Unit>
        {
            var attempt = Interlocked.Increment(ref CallCount);
            if (_onCall is not null) await _onCall(attempt, ct);
        }
    }

    [Fact]
    public async Task NoPolicies_PassthroughAndCallsInnerOnce()
    {
        var inner = new CountingDispatcher();
        var sut = new ResilientSagaCommandDispatcher(inner, new SagaResilienceOptions());

        await sut.DispatchAsync(new Cmd(1), CancellationToken.None);

        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Retry_RecoversFromTransientFailure()
    {
        var inner = new CountingDispatcher((attempt, _) =>
            attempt < 3 ? Task.FromException(new InvalidOperationException("transient")) : Task.CompletedTask);
        var options = new SagaResilienceOptions
        {
            Retry = new RetryPolicy(maxAttempts: 5, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0),
        };
        var sut = new ResilientSagaCommandDispatcher(inner, options);

        await sut.DispatchAsync(new Cmd(1), CancellationToken.None);

        Assert.Equal(3, inner.CallCount);
    }

    [Fact]
    public async Task Retry_ExhaustsBudget_ThrowsResilienceExceptionWithInner()
    {
        var inner = new CountingDispatcher((_, _) => Task.FromException(new InvalidOperationException("always")));
        var options = new SagaResilienceOptions
        {
            Retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0),
        };
        var sut = new ResilientSagaCommandDispatcher(inner, options);

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            async () => await sut.DispatchAsync(new Cmd(1), CancellationToken.None));

        Assert.Equal(ResiliencePolicy.Retry, ex.Policy);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(3, inner.CallCount);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAfterFailureThreshold_ShortCircuits()
    {
        var inner = new CountingDispatcher((_, _) => Task.FromException(new InvalidOperationException("always")));
        using var breaker = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 60_000, halfOpenProbes: 1);
        var options = new SagaResilienceOptions { CircuitBreaker = breaker };
        var sut = new ResilientSagaCommandDispatcher(inner, options);

        // Trip the breaker.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.DispatchAsync(new Cmd(1), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.DispatchAsync(new Cmd(2), CancellationToken.None));

        Assert.Equal(CircuitBreakerState.Open, breaker.State);

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            async () => await sut.DispatchAsync(new Cmd(3), CancellationToken.None));

        Assert.Equal(ResiliencePolicy.CircuitBreaker, ex.Policy);
        // Inner was called 2 times (the first two), but NOT the short-circuited third.
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task RateLimit_ExhaustedBucket_ThrowsResilienceException()
    {
        var inner = new CountingDispatcher();
        var limiter = new RateLimiter(maxPerSecond: 1, burstSize: 1, scope: RateLimitScope.Instance);
        var options = new SagaResilienceOptions { RateLimiter = limiter };
        var sut = new ResilientSagaCommandDispatcher(inner, options);

        await sut.DispatchAsync(new Cmd(1), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            async () => await sut.DispatchAsync(new Cmd(2), CancellationToken.None));

        Assert.Equal(ResiliencePolicy.RateLimit, ex.Policy);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task Timeout_InnerExceedsBudget_ThrowsResilienceException()
    {
        var inner = new CountingDispatcher(async (_, ct) => await Task.Delay(500, ct));
        var options = new SagaResilienceOptions { Timeout = new TimeoutPolicy(totalMs: 50) };
        var sut = new ResilientSagaCommandDispatcher(inner, options);

        var ex = await Assert.ThrowsAsync<ResilienceException>(
            async () => await sut.DispatchAsync(new Cmd(1), CancellationToken.None));

        Assert.Equal(ResiliencePolicy.Timeout, ex.Policy);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceled_NotResilienceException()
    {
        var inner = new CountingDispatcher(async (_, ct) =>
        {
            await Task.Delay(500, ct);
        });
        var options = new SagaResilienceOptions
        {
            Retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0),
        };
        var sut = new ResilientSagaCommandDispatcher(inner, options);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sut.DispatchAsync(new Cmd(1), cts.Token));
    }

    [Fact]
    public async Task RetryWithCircuitBreaker_FailureCountsCascade()
    {
        // Two retries × 2 max-failures-to-trip means a single batch of "always fails"
        // calls trips the breaker. The inner is called twice (the retry budget),
        // both failures are observed by the breaker, then the breaker is Open.
        var inner = new CountingDispatcher((_, _) => Task.FromException(new InvalidOperationException("always")));
        using var breaker = new CircuitBreakerPolicy(maxFailures: 2, resetMs: 60_000, halfOpenProbes: 1);
        var options = new SagaResilienceOptions
        {
            Retry = new RetryPolicy(maxAttempts: 2, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0),
            CircuitBreaker = breaker,
        };
        var sut = new ResilientSagaCommandDispatcher(inner, options);

        await Assert.ThrowsAsync<ResilienceException>(
            async () => await sut.DispatchAsync(new Cmd(1), CancellationToken.None));

        Assert.Equal(CircuitBreakerState.Open, breaker.State);
        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ResilientSagaCommandDispatcher(null!, new SagaResilienceOptions()));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ResilientSagaCommandDispatcher(new CountingDispatcher(), null!));
    }
}
