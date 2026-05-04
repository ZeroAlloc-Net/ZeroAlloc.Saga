# Phase 3b — `ZeroAlloc.Saga.Resilience` Design

**Date:** 2026-05-04
**Status:** Implementation in progress
**Roadmap entry:** v1.3 in README.md (paired originally with Saga.Outbox; deferred from Phase 3a)

## Goal

Wrap saga step-command dispatch in a `ZeroAlloc.Resilience` policy (retry / timeout / circuit-breaker / rate-limit) so transient receiver-side failures don't kill the saga. Opt-in via a single fluent call.

## Where the resilience wrap goes

Three candidate dispatch paths exist; the wrap-point matters because they have different failure-mode shapes:

| Path | What dispatches | Failure shape | Wrap value |
|---|---|---|---|
| **No-outbox sync** | `handler → MediatorSagaCommandDispatcher → IMediator.Send` synchronously | Transient receiver-side failures (DB hiccup, network blip, throttled API) | **High** — retries kick in for the exact case the policy is designed for. |
| **Outbox write side** | `handler → OutboxSagaCommandDispatcher → EnqueueDeferred` | Local — serialiser throws or DI lookup fails; rarely transient. | Low. |
| **Outbox delivery side** | `OutboxSagaCommandPoller → SagaCommandRegistry.DispatchAsync → IMediator.Send` | Same as no-outbox sync, but at poll cadence. | High, but poller already has its own retry-and-dead-letter loop. |

**v1 decision: cover only the no-outbox sync path.** `WithResilience()` decorates the current `ISagaCommandDispatcher` registration. If the user has called `WithOutbox()`, `WithResilience()` after that wraps the outbox dispatcher (low value but harmless) — call order is documented. The outbox poller already has its own `OutboxSagaPollerOptions.MaxRetries` + `RetryDelay` + dead-letter, which is the appropriate retry surface for the delivery side.

A v2 follow-up could plug into the poller's reflective dispatch delegate to apply a configured `ResiliencePolicy` to actual delivery — that's a separate iteration once we have a host asking for both outbox + receiver-side retry composition.

## Architecture

**One new package:** `ZeroAlloc.Saga.Resilience` 1.0.0.

**Public surface:**

```csharp
namespace ZeroAlloc.Saga.Resilience;

public sealed class SagaResilienceOptions
{
    public RetryPolicy? Retry { get; set; }
    public TimeoutPolicy? Timeout { get; set; }
    public CircuitBreakerPolicy? CircuitBreaker { get; set; }
    public RateLimiter? RateLimiter { get; set; }
}

public sealed class ResilientSagaCommandDispatcher : ISagaCommandDispatcher
{
    public ResilientSagaCommandDispatcher(ISagaCommandDispatcher inner, SagaResilienceOptions options);
    public ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>;
}

public static class SagaResilienceBuilderExtensions
{
    public static ISagaBuilder WithResilience(
        this ISagaBuilder builder,
        Action<SagaResilienceOptions> configure);
}
```

**Composition order inside `DispatchAsync`:** `circuit-breaker → rate-limit → timeout → retry → inner`. Outermost first, so a tripped breaker short-circuits before the rate limiter touches its bucket; rate-limit before timeout so a denied token never starts the timeout clock; timeout before retry so each attempt has a per-attempt deadline.

**Why hand-rolled, not the Resilience source-gen proxy:** `ISagaCommandDispatcher.DispatchAsync<TCommand>` is a *generic method*, and the `ZeroAlloc.Resilience` proxy generator currently emits per-method proxies based on interface attributes. Generic methods are an edge case the generator doesn't handle as cleanly as type-level generics (cf. `IOutboxDispatcher<T>` where T is type-level). A small hand-coded wrapper is more reliable and more transparent.

## Wiring example

```csharp
services.AddSaga()
    .WithEfCoreStore<AppDbContext>(opts => opts.MaxRetryAttempts = 3)
    .WithResilience(r =>
    {
        r.Retry = new RetryPolicy(maxAttempts: 5, backoffMs: 200, jitter: true, perAttemptTimeoutMs: 0);
        r.CircuitBreaker = new CircuitBreakerPolicy(maxFailures: 10, resetMs: 30_000, halfOpenProbes: 1);
    })
    .AddOrderFulfillmentSaga();
```

## Composition with `WithOutbox()`

The `WithResilience()` extension does `services.Decorate<ISagaCommandDispatcher>(...)` (manual decorator pattern — `Microsoft.Extensions.DependencyInjection` doesn't ship `Decorate` so we hand-roll it). Order:

- `.WithResilience().WithOutbox()` — resilience wraps the default `MediatorSagaCommandDispatcher`, then `WithOutbox()` calls `Replace()` and the resilience wrap is gone. **Bug-prone.** Document: WithResilience comes AFTER WithOutbox if both are used.
- `.WithOutbox().WithResilience()` — outbox replaces, resilience decorates. The wrap covers `OutboxSagaCommandDispatcher.DispatchAsync` (the enqueue). Low value (enqueue is local) but does no harm.

To avoid surprise, `WithResilience()` checks for `OutboxSagaCommandDispatcher` registration and emits an `ILogger.LogWarning` at first invocation: "WithResilience() detected a WithOutbox() registration; the resilience policy wraps the enqueue path which rarely sees transient failures. For receiver-side resilience under outbox, configure OutboxSagaPollerOptions or wait for v2 (poller-side resilience)."

## Compatibility

- New package. Does not affect existing `ZeroAlloc.Saga` or `ZeroAlloc.Saga.EfCore` consumers.
- No generator changes — pure DI-side decorator.
- No public API change to `ISagaCommandDispatcher`.
- Compatible with InMemory backend, EfCore backend, Outbox bridge.

## Out of scope (v1)

- Per-command-type policies (`[ResiliencePolicy("PaymentApi")]` on the command type). v1 has one global policy. Per-command would need either source-gen proxies (the generic-method problem above) or a registry keyed by `typeof(TCommand)` — the latter is a clean v2 path.
- Outbox poller-side resilience. Documented in the `When to use` section above.
- Telemetry/metrics. The Resilience policies expose state via their public APIs (`CircuitBreakerPolicy.State`, etc.); a future `Saga.Telemetry` bridge can surface these.

## Test plan

1. Unit tests for `ResilientSagaCommandDispatcher`:
   - Retry: inner throws transient, retried, eventual success → exactly N attempts observed.
   - Retry exhaustion: inner always throws → `RetryPolicy.MaxAttempts` reached, exception bubbles.
   - Timeout: inner takes too long → `TimeoutException` (or `ResilienceException` per package).
   - CircuitBreaker: enough failures → breaker opens, subsequent calls short-circuit.
   - Rate limit: more calls than budget → token-bucket denial.
   - All policies disabled (default options) → exact passthrough, no allocation.
   - Composition: retry inside timeout — per-attempt timeout fires on attempt 1, retry kicks in.

2. Integration test:
   - Build saga + WithResilience, mediator-handler throws on first call, succeeds on second → saga completes; ledger sees 2 dispatches (the retry).

3. Compatibility test:
   - `.WithOutbox().WithResilience()` chain wires both; `LogWarning` fired (asserted via test logger).

## Sample

Add `samples/ResilienceSample/` (small, JIT only) that demonstrates retry of a flaky `IMediator.Send` handler. Not an AOT smoke — adding AotSmokeResilience is a follow-up if the policies turn out to need AOT-rooting attention (they likely don't; pure runtime decorator).
