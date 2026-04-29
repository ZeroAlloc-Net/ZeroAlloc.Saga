# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 1.0.0

Initial stable release of `ZeroAlloc.Saga` — source-generated, AOT-compatible
multi-step saga orchestration for the ZeroAlloc ecosystem.

### Summary

`ZeroAlloc.Saga` lets you express long-running business workflows as a
partial class. The source generator emits a state-machine, one
`INotificationHandler<T>` per subscribed event, a correlation dispatcher,
and AOT-safe DI registrations. Compensation runs in reverse declaration
order on failure events tagged with `CompensateOn` (or via the
`ISagaManager.CompensateAsync` ops API). InMemory persistence is the
default v1.0 backend; durable backends ship in v1.1.

### Features

- **Authoring attributes:** `[Saga]`, `[Step(Order, Compensate, CompensateOn)]`,
  `[CorrelationKey]` — a saga is a partial class, nothing more.
- **Source generator:** emits an FSM companion class, partial-class
  completion, one notification handler per event, a correlation-dispatch
  helper, and an `AddXxxSaga()` DI extension per `[Saga]` class.
- **Compensation:** reverse-cascade compensation triggered automatically
  by `CompensateOn` failure events, or operationally by
  `ISagaManager<TSaga, TKey>.CompensateAsync(key, ct)`.
- **Type-safe correlation:** `[CorrelationKey]` methods per event type;
  the generator validates that every step's input event (and every
  `CompensateOn` event) has a matching key extractor.
- **Per-saga concurrency:** `SagaLockManager<TKey>` keyed by correlation
  ID serializes events for the same saga while letting different saga
  instances run in parallel.
- **InMemory persistence:** `InMemorySagaStore<TSaga, TKey>` —
  `ConcurrentDictionary`-backed, by-reference semantics, suitable for
  tests, prototypes, and single-process deployments.
- **Native AOT-compatible:** no reflection, no open-generic resolution
  at runtime, concrete-closed-type DI registrations. CI publishes
  `samples/AotSmoke/` with `PublishAot=true` on every push and asserts
  the saga reaches `Completed` and is removed from the store.
- **13 source-generator diagnostics** (`ZASAGA001`-`ZASAGA013`) covering
  partial declarations, shape errors, correlation-key contracts, step-order
  contiguity, compensation method binding, and cross-saga subscription
  mismatches.
- **3 code-fix providers:** `ZASAGA001` (Make partial), `ZASAGA007`
  (Renumber steps), `ZASAGA009` (Add compensation method).
- **Documentation:** `docs/concepts.md`, `docs/correlation.md`,
  `docs/compensation.md`, `docs/diagnostics.md` — diagnostic help links
  resolve to the diagnostics page.
- **Samples:** `samples/OrderFulfillment/` (full demo with happy path,
  compensation, orphan handling, and manual compensation),
  `samples/AotSmoke/` (CI-published).

### Public API surface

```csharp
namespace ZeroAlloc.Saga;
public sealed class SagaAttribute : Attribute;
public sealed class StepAttribute : Attribute { int Order; string? Compensate; Type? CompensateOn; }
public sealed class CorrelationKeyAttribute : Attribute;

public interface ISagaStore<TSaga, TKey>      where TSaga : class, new() where TKey : notnull, IEquatable<TKey>;
public interface ISagaManager<TSaga, TKey>    where TSaga : class, new() where TKey : notnull, IEquatable<TKey>;
public interface ISagaBuilder;
public sealed class InMemorySagaStore<TSaga, TKey>;
public sealed class SagaManager<TSaga, TKey>;
public sealed class SagaLockManager<TKey>;
public static class SagaServiceCollectionExtensions { ISagaBuilder AddSaga(this IServiceCollection); }
```

Per `[Saga]` class the generator extends `ISagaBuilder` with an
`AddXxxSaga()` method that registers every concrete-closed-type pair.

### Hard dependencies

- `ZeroAlloc.Mediator` 2.0.x — `INotification`, `IRequest`, `IMediator.Send`
- `Microsoft.Extensions.DependencyInjection` 9.0.x — DI surface
- `Microsoft.Extensions.Logging.Abstractions` 9.0.x — saga-handler loggers

### Known limitations (deferred to later phases)

- **InMemory is not durable** — process crash loses all in-flight sagas.
  Durable backends (`ZeroAlloc.Saga.EfCore`, `ZeroAlloc.Saga.Redis`) ship
  in v1.1. Documented prominently in README.
- **`SagaLockManager<TKey>` grows monotonically** — ~80 bytes per unique
  correlation key, never evicted. Bounded eviction lands in v1.x for
  high-cardinality workloads (BACKLOG #17).
- **No timeouts / deadlines.** v1.0 sagas wait indefinitely for the
  next event. v1.3 adds `[Step(TimeoutMs = ...)]` via the Scheduling
  integration.
- **No telemetry.** No spans, counters, or histograms. v1.4 ships
  `ZeroAlloc.Saga.Telemetry` bridge.
- **No serialization / rehydration.** `TSaga` is held by reference;
  durable backends in v1.1 add the snapshot helpers (BACKLOG #13).
- **No saga-state versioning / migration** — v1.1+ (BACKLOG #14).
- **No explicit start API** — `OrderPlaced` (or any first-step event)
  is the implicit start. Explicit start surface in v1.x (BACKLOG #15).
- **No choreography mode** — v1.0 is orchestration-only. Choreography
  is a v1.5 stretch (BACKLOG #12).
- **No EventSourcing-backed store** — v1.5 stretch via
  `ZeroAlloc.Saga.EventSourcing` (separate ES feature).
- **Diagnostics scope** — v1.0 ships 13 diagnostics; full unreachable-step
  analysis, step-input/output type-chain validation, and cycle detection
  in the step graph are deferred to v1.x (BACKLOG #11).

### Migration notes

None — this is the first release, and there is nothing prior to migrate
from. Subsequent minor versions will follow Semantic Versioning.

### Acknowledgements

`ZeroAlloc.Saga` v1.0 is the capstone of a campaign across the ZeroAlloc
ecosystem that built up to it:

- `ZeroAlloc.Mediator 2.0` — the notification + request dispatch surface
  that sagas subscribe to and emit through.
- `ZeroAlloc.StateMachine 1.0` — the FSM-on-a-partial-class pattern that
  the saga generator emits as a companion type.
- `ZeroAlloc.Pipeline` — the behaviours pipeline composed transitively
  via Mediator.
- `ZeroAlloc.Resilience.Generator` — the bundled-generator-as-analyzer
  packaging precedent that `ZeroAlloc.Saga` follows (single nupkg, no
  separate `.Generator` install).
- The `[TypedId]` convention from earlier ecosystem work — what makes
  strongly-typed correlation keys (`OrderId(int)` etc.) idiomatic at
  the call site.

Without that platform underneath, Saga would have to reinvent dispatch,
state-machine encoding, and packaging conventions from scratch. Instead,
it composes them.
