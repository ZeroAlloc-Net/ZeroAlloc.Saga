# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/compare/v1.0.0...v1.1.0) (2026-04-29)


### Features

* **saga.generator:** AddXxxSaga() implicitly calls AddMediator() and routes through SagaStoreRegistrar ([eecf72c](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/eecf72cc371ee892efe377a24cc0b08afec4ec20))
* **saga.generator:** emit Snapshot/Restore + ISagaPersistableState per saga ([d1dc37a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/d1dc37a8cf3a21123864374daa2472b9d9429963))
* **saga.generator:** extract StateFieldInfo + ZASAGA014/015 descriptors ([796f8e2](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/796f8e202eb340996f345d1e5c61707c15d16c3f))
* **saga.generator:** wire ZASAGA015 syntactic backend detection + diagnostic tests ([fc98b7b](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/fc98b7b4eb7bedfcaa000e08c0eea2e7b094c9ca))
* **saga:** add [NotSagaState] escape-hatch attribute ([bb5d2e1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/bb5d2e10edc85a814bc0062caac2593c03406b10))
* **saga:** add ISagaPersistableState interface for backend state round-trip ([8ae2400](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/8ae240076429954b49fea70672878c771b7144a7))
* **saga:** add SagaStateReader ref struct mirroring writer ([a077b9a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/a077b9aaf250e34fc7c46a043a57c424a3497303))
* **saga:** add SagaStateVersionMismatchException + SagaConcurrencyException ([e1bc7f8](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/e1bc7f8e6c198c509d27f0e8b7b9813c9e39df80))
* **saga:** add SagaStateWriter ref struct for state serialization ([683b7d5](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/683b7d5949a9c700c97dcdbc3458e15db878c7bf))
* **saga:** expose IsEfCoreBackend flag + SagaStoreRegistrar indirection ([f3383d0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/f3383d036ec0a69c7eb8fa9d153e7861407e571d))
* **saga:** v1.1 — ISagaPersistableState, byte serializer, ZASAGA014/015 ([5ac16a4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/5ac16a4013f9034e887a113a983f7e9f268ac3c1))


### Bug Fixes

* **saga.generator:** drop pointless default! init on nullable Restore temp ([ac9e7bb](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/ac9e7bb3f2fe7cbfd74c36e85f693e09c303d0fa))
* **saga.generator:** emit predefined C# keywords without global:: prefix ([42a1a00](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/42a1a003f309330542da70e237d2ec46e2a50a2c))
* **saga:** drop framework-convention auxiliary ctors on sealed exceptions ([ca45925](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/ca45925f47fd9920ac9c8d29bc0094737e87b0d6))
* **saga:** preserve null-vs-empty round-trip for byte[]? state fields ([489e5bc](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/489e5bc9f931eafc64121fa95149d2c58104217d))


### Documentation

* prep for v1.1 release ([7e5a1ed](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/7e5a1edac476a8605380b64d892268e69490776d))


### Miscellaneous Chores

* **release:** tag v1.1.0 ([7ac8399](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/7ac83990fc6eac0bab2fc022c9a26100154d1edf))

## 1.1.0

Additive release that prepares the runtime + generator for v1.1 durable
backends. The first durable backend, `ZeroAlloc.Saga.EfCore` 1.0.0, ships
in subsequent PRs from the same repo.

### Features

- **`ISagaPersistableState`** — every `[Saga]` class implements it via a
  generator-emitted partial. Backends use the interface to round-trip
  saga state across process boundaries.
- **`SagaStateWriter` / `SagaStateReader`** — zero-allocation, AOT-safe
  ref structs for typed binary serialization. Cover all primitives,
  `string` (UTF-8), `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`,
  `byte[]`, plus Nullable wrappers.
- **`SagaStateVersionMismatchException`** + **`SagaConcurrencyException`** —
  surfaced by backends on version-byte mismatch and exhausted OCC retries.
- **`[NotSagaState]`** — escape hatch attribute to exclude a property/field
  from generator-emitted Snapshot/Restore.
- **Generator emits Snapshot/Restore** per `[Saga]` class via field
  inspection — supports primitives, enums, `string`, well-known structs
  (DateTime/DateTimeOffset/TimeSpan/Guid), `[TypedId]`-attributed types
  and the common `record struct Foo(TPrim Bar)` shape, `byte[]`, and
  Nullable wrappers thereof.
- **Conditional store registration** — generator-emitted `AddXxxSaga()`
  inspects `ISagaBuilder.IsEfCoreBackend` at composition time. Default
  remains `InMemorySagaStore<,>`; durable backends flip the flag and
  install their concrete store via the new `SagaStoreRegistrar`
  indirection point.
- **Implicit `AddMediator()`** — generator-emitted `AddXxxSaga()` now
  calls `services.AddMediator()` first. Users no longer need a separate
  `services.AddMediator()` call before `AddSaga()`.
- **2 new diagnostics**:
  - `ZASAGA014` (Error) — Saga state field has an unsupported type.
    Tells the user what's supported and how to escape with `[NotSagaState]`.
  - `ZASAGA015` (Info, suppressible) — saga commands should be idempotent
    under durable backends. Fires when `WithEfCoreStore`/`WithRedisStore`
    is detected in the same compilation.

### Public API additions

```csharp
namespace ZeroAlloc.Saga;
public interface ISagaPersistableState
{
    byte[] Snapshot();
    void Restore(ReadOnlySpan<byte> data);
    string CurrentFsmStateName { get; }
    void SetFsmStateFromName(string stateName);
}
public readonly ref struct SagaStateWriter;
public ref struct SagaStateReader;
public sealed class SagaStateVersionMismatchException : Exception;
public sealed class SagaConcurrencyException : Exception;
[AttributeUsage(...)] public sealed class NotSagaStateAttribute : Attribute;
public interface ISagaBuilderMutable { bool IsEfCoreBackend { get; set; } }
public static class SagaBuilderMutationExtensions { void SetEfCoreBackend(this ISagaBuilder); }
public static class SagaStoreRegistrar
{
    static void SetRegistrar(Action<ISagaBuilder> registrar);
    static void Apply<TSaga, TKey>(ISagaBuilder builder);
    static void OverrideStore<TSaga, TKey, TStore>(ISagaBuilder builder);
}
```

### Backwards compatibility

- Existing `[Saga]` classes from v1.0 continue to compile and run unchanged.
  The new `Snapshot()`/`Restore()` methods are emitted by the generator
  automatically as part of the partial-class completion.
- `InMemorySagaStore<,>` remains the default backend — no behavioural
  change for users who don't opt into a durable backend.
- `AddSaga()`'s shape is unchanged; the implicit `AddMediator()` is
  idempotent (`TryAdd*`) so users who still call it explicitly suffer
  nothing.

## 1.0.0 (2026-04-29)


### Features

* add ISagaBuilder + AddSaga() DI extension ([cce4e12](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/cce4e129981ea481d64a04c54f8429866dd47f63))
* add ISagaManager + SagaManager + ISagaCompensationDispatcher ([3ea663f](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/3ea663fac7f8b9e1119e7bd9fffd588430e84a03))
* add ISagaStore&lt;TSaga,TKey&gt; + InMemorySagaStore default ([42f2dba](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/42f2dba935370d76bfa4cd54b64bd2001eaefb5f))
* add public Saga, Step, CorrelationKey attributes ([8386152](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/83861522d35e23d877c29486a845d0a6189ca2b7))
* add SagaLockManager&lt;TKey&gt; for per-saga serialization ([0b8ca8a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/0b8ca8a4c9588570de9e7a4d3e7b7d4acfcf8c4c))
* AOT smoke + sample + docs + 1.0.0 release ([8423492](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/842349245aa2b88c1b54ffa89b93d40751784220))
* diagnostics ZASAGA001-013 + 3 code-fix providers ([fb8a2ca](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/fb8a2cacb03180c7eb19677ce2e8a550b8dabb5e))
* **diagnostics:** code-fix providers for ZASAGA001/007/009 ([cd3dc3d](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/cd3dc3d61162a4ea18eb9705e798ef8a9498a997))
* **diagnostics:** wire ZASAGA001-013 reporting into generator ([8b87196](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/8b871966bf7daa54100359724c47b486c395e06e))
* **generator:** emit AOT-safe AddXxxSaga DI extension and compensation dispatcher ([60d9db4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/60d9db4a32e90ab29bbeebac84af0e70fca3ddcc))
* **generator:** emit Fsm property on user's saga partial class ([19e4fd0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/19e4fd09318f2f31106d1bda324e9d43afbec7c2))
* **generator:** emit inline FSM partial class per [Saga] ([0f4cebd](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/0f4cebdeb7f3c4930228a6a8e91e4d58ed31f68a))
* **generator:** emit per-event INotificationHandler with forward and compensation cascade ([23ea815](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/23ea815f0007fb380af16e04f1c921fe6f4b894a))
* **generator:** emit static correlation-dispatch class per [Saga] ([5c638b1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/5c638b1da9d8677913a3ba67996bb0d40c482988))
* **generator:** SagaGenerator skeleton + SagaModel ([0072efa](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/0072efa24ce138c3dceb345c69a15660bf482dc9))
* runtime + source generator + happy-path tests ([19647f2](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/19647f23cc0f0a7b03042c6f9d9b6e54e8e0e666))
* **samples:** add AotSmoke — minimal saga e2e for AOT publish ([3509c61](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/3509c61c0f5d5cfce2c56983aae7b746d9e7ccc9))
* **samples:** add OrderFulfillment demo ([00eb426](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/00eb4263f376be700827cfc5daec2b0b0dd6f4cc))


### Documentation

* **changelog:** add 1.0.0 entry ([1e91e37](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/commit/1e91e374095e6dd78e859195de8cbad1fe2c1ce5))

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
