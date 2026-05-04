# Resilient saga dispatch with `ZeroAlloc.Saga.Resilience`

`ZeroAlloc.Saga.Resilience` is an opt-in bridge that wraps every saga step
command's dispatch in `ZeroAlloc.Resilience` policies — retry, timeout,
circuit-breaker, rate-limit. Configure once on the `ISagaBuilder`; every
generator-emitted handler's call to `ISagaCommandDispatcher.DispatchAsync`
runs through the configured pipeline.

> **Status:** ships in v1.x alongside `ZeroAlloc.Saga` 1.4+ and
> `ZeroAlloc.Saga.Outbox` 1.3+. Requires `ZeroAlloc.Resilience` 1.0+.

## What it fixes

A saga step's command typically goes through `IMediator.Send` to a
receiver-side handler. Receivers fail transiently — a downstream API
returns 503, the database is overloaded, the network blips. Without
resilience, every transient failure becomes a saga retry through the
EF Core OCC retry loop (which exists for *concurrency* conflicts, not
receiver flakiness) and burns through `MaxRetryAttempts` quickly.

`WithResilience()` adds a layer between the saga handler and the
mediator. The default `MediatorSagaCommandDispatcher` is decorated with
`ResilientSagaCommandDispatcher`, which composes the configured
policies in the canonical outermost-first order:

```text
circuit-breaker → rate-limit → timeout → retry → inner.DispatchAsync
```

A tripped breaker short-circuits before the rate limiter touches its
bucket. Rate-limit denial happens before the timeout clock starts.
Timeout bounds total wall-clock time across all retries. Retry runs
the inner with optional per-attempt timeout, exponential backoff, and
optional jitter.

## When to use it

| Scenario | Recommendation |
|---|---|
| Receiver-side transient failures (HTTP 5xx, network blip) | **Use it.** Retry policy wraps `IMediator.Send`. |
| Downstream API is occasionally rate-limited or rejecting load | Use a circuit-breaker — saga short-circuits on Open instead of grinding through the retry budget. |
| `WithOutbox()` already wired | The bridge wraps the **enqueue** path, not the delivery path. Receiver-side retries belong in `OutboxSagaPollerOptions` (which has its own retry/dead-letter built in). See "Composition with the outbox bridge" below. |
| InMemory backend + simple in-process saga | Optional. The InMemory store doesn't OCC-conflict, so the only failure source is the receiver — retry is helpful. |

## Wiring

```csharp
services.AddMediator();
services.AddSaga()
    .WithEfCoreStore<AppDbContext>(opts => opts.MaxRetryAttempts = 3)
    .AddOrderFulfillmentSaga()                  // <-- registers ISagaCommandDispatcher
    .WithResilience(r =>                        // <-- decorates it (must come AFTER)
    {
        r.Retry = new RetryPolicy(
            maxAttempts: 5,
            backoffMs: 200,
            jitter: true,
            perAttemptTimeoutMs: 5_000);
        r.CircuitBreaker = new CircuitBreakerPolicy(
            maxFailures: 10,
            resetMs: 30_000,
            halfOpenProbes: 1);
    });
```

`WithResilience` decorates the **currently registered**
`ISagaCommandDispatcher`, so it must come **after** the per-saga
registration that installs it. Generator-emitted
`Add{Saga}Saga()` is what registers the default dispatcher (and
`WithOutbox()` replaces it). If `WithResilience` is called before
`Add{Saga}Saga()`, it throws `InvalidOperationException` with a
helpful message.

Order matters when combined with other dispatcher-replacing
extensions:

- `.AddXxxSaga().WithResilience().WithOutbox()` — `WithOutbox`
  re-`Replace`s the registration after `WithResilience` decorates,
  so the resilience layer is **lost**. Don't write it this way.
- `.AddXxxSaga().WithOutbox().WithResilience()` — outbox replaces,
  resilience decorates the outbox dispatcher. Functional but wraps
  the enqueue path, which has limited value (see the next section);
  the dispatcher logs a one-shot warning at first resolve when this
  shape is detected.

## Composition with the outbox bridge

The outbox bridge defers actual delivery to a poller. The dispatcher
the saga handler invokes does only one thing: serialise the command
and `Add` a tracked `OutboxMessageEntity` to the scoped `DbContext`.
This path has very few transient failure modes — the serialiser
throws (deterministic, not retriable) or DI fails to find an
`ISerializer<T>` (deterministic).

Receiver-side resilience under outbox lives one layer further out, in
`OutboxSagaPollerOptions`:

```csharp
services.Configure<OutboxSagaPollerOptions>(o =>
{
    o.MaxRetries = 5;
    o.RetryDelay = TimeSpan.FromSeconds(30);
    // Plus the poller's built-in dead-letter when MaxRetries is hit.
});
```

The poller's per-entry retry is the right injection point for
"receiver returned 503, try again later." A future v2 of
`Saga.Resilience` may plug a `ResiliencePipeline` directly into the
poller's reflective `SagaCommandRegistry.DispatchAsync` invocation
to bring full circuit-breaker + rate-limit semantics to the delivery
layer; the v1 surface is the no-outbox synchronous path.

## Policy primitives

All policy types are from `ZeroAlloc.Resilience`. Brief reference:

### `RetryPolicy(maxAttempts, backoffMs, jitter, perAttemptTimeoutMs)`

- `maxAttempts` — total attempts including the initial call.
- `backoffMs` — base backoff. Per-attempt delay is `backoffMs * 2^attempt`.
- `jitter` — adds random 0–50% jitter to each delay.
- `perAttemptTimeoutMs` — per-attempt cancellation deadline. `0` disables.

When the budget is exhausted, the dispatcher throws
`ResilienceException(Policy: Retry, InnerException: <last failure>)`.

### `TimeoutPolicy(totalMs)`

Bounds total wall-clock time across all retries. The caller's
`CancellationToken` is linked into the timeout — caller cancellation
still propagates as `OperationCanceledException`. A pure timeout
elapsed surfaces as `ResilienceException(Policy: Timeout)`.

### `CircuitBreakerPolicy(maxFailures, resetMs, halfOpenProbes)`

- `maxFailures` — consecutive failures that trip Closed → Open.
- `resetMs` — milliseconds before Open → HalfOpen probe.
- `halfOpenProbes` — successes required to close from HalfOpen.

When Open, dispatch short-circuits with
`ResilienceException(Policy: CircuitBreaker)` without invoking the
inner. The breaker is a long-lived process-wide object — register it
as a singleton in DI if you want one breaker per `IMediator`-wide
endpoint, or create one per `WithResilience()` call (the current
shape) for a saga-scoped breaker.

### `RateLimiter(maxPerSecond, burstSize, scope)`

Lock-free token bucket. `TryAcquire()` returns `false` when empty;
the dispatcher surfaces this as
`ResilienceException(Policy: RateLimit)`.

## Exceptions

The dispatcher distinguishes three failure shapes:

1. **`OperationCanceledException`** — caller's `CancellationToken`
   was cancelled. Propagates unchanged. NOT retried, NOT counted as
   a circuit-breaker failure. Total-timeout firings ARE distinguished
   from caller cancellation: the dispatcher checks the caller's CT
   first and surfaces a cancel as `OperationCanceledException`,
   reserving `ResilienceException(Timeout)` for the case where only
   the timeout token fired.
2. **`ResilienceException`** — a policy itself denied or exhausted
   (retry budget, timeout, breaker open, rate-limit empty). Inspect
   `Policy` to disambiguate.
3. **Other exceptions** — bubble through after the policies have
   their say. Any exception observed by the inner is recorded by the
   circuit breaker; if the retry budget is configured, the exception
   may be re-thrown after `MaxAttempts` as a `ResilienceException`.

### Timeout-vs-breaker semantics

A total-timeout firing while the inner is still running surfaces as
`ResilienceException(Timeout)`. The breaker is **NOT** informed of the
timeout — it sees only inner exceptions. If you want timeouts to count
toward the breaker's failure budget, prefer
`RetryPolicy.PerAttemptTimeoutMs` over `TimeoutPolicy.TotalMs`: a
per-attempt timeout fires inside the inner's `Task.Delay` /
`HttpClient.SendAsync` etc. as an `OperationCanceledException`, which
the inner typically wraps or rethrows in a way the breaker observes.
The total `TimeoutPolicy` is the right tool when the retry budget is
also configured and you want a single wall-clock deadline across all
retries — the breaker correctly stays out of the way then.

### Retry exhaustion and breaker accounting

Each inner failure during retry is reported to the breaker via
`OnFailure` BEFORE the next attempt's backoff. So a 3-attempt retry
that always fails contributes 3 failures to the breaker's count,
not 1. This is intentional: the breaker measures real failure rate,
not "did the dispatcher eventually give up." If your breaker
threshold is calibrated for a single attempt, divide it by your
typical retry count, or use a separate breaker per call site.

## Limitations

- **No per-command-type policies.** v1 has one global policy. If you
  need different retry budgets per command, register a single saga
  with multiple `WithResilience` blocks scoped under different
  builders (one builder per saga group), or wait for v2.
- **Outbox poller-side resilience.** Documented above. v1 doesn't
  reach into the poller's reflective dispatch path.
- **No telemetry hooks.** `CircuitBreakerPolicy.State` is publicly
  readable, but the bridge doesn't yet emit OTel spans/metrics for
  retry counts, breaker transitions, etc. A future
  `Saga.Telemetry` bridge will surface these.

## See also

- [`docs/persistence-efcore.md`](persistence-efcore.md) — base
  `Saga.EfCore` setup, OCC retry, idempotency.
- [`docs/outbox.md`](outbox.md) — atomic dispatch + the
  `OutboxSagaPollerOptions` retry knobs.
- `ZeroAlloc.Resilience` — full attribute and policy reference.
