# ZeroAlloc.Saga

Source-generated long-running process orchestration for the ZeroAlloc ecosystem.

> **Status:** v1.1 — AOT compatible, single-package install. v1.1 adds
> `ISagaPersistableState` + byte serializer (paving the way for durable
> backends; `ZeroAlloc.Saga.EfCore` ships from the same repo immediately
> after this release). InMemory remains the default backend.

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Saga.svg)](https://www.nuget.org/packages/ZeroAlloc.Saga)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/actions/workflows/build.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

## Overview

`ZeroAlloc.Saga` lets you express multi-step business workflows declaratively
as a partial class. The source generator emits state-machine code,
notification handlers, and dispatch wiring. Compensation runs in reverse on
failure. No reflection, no open-generic resolution at runtime — the whole
runtime is Native AOT-compatible and exercised by an `aot-smoke` CI job
that publishes a sample with `PublishAot=true` and asserts end-to-end execution.

```csharp
[Saga]
public partial class OrderFulfillmentSaga
{
    public OrderId OrderId { get; private set; }
    public decimal Total { get; private set; }

    [CorrelationKey] public OrderId Correlation(OrderPlaced e)     => e.OrderId;
    [CorrelationKey] public OrderId Correlation(StockReserved e)   => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentCharged e)  => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;

    [Step(Order = 1, Compensate = nameof(CancelReservation))]
    public ReserveStockCommand ReserveStock(OrderPlaced e)
    {
        OrderId = e.OrderId; Total = e.Total;
        return new ReserveStockCommand(e.OrderId, e.Total);
    }

    [Step(Order = 2, Compensate = nameof(RefundPayment), CompensateOn = typeof(PaymentDeclined))]
    public ChargeCustomerCommand ChargeCustomer(StockReserved e) => new(OrderId, Total);

    [Step(Order = 3)]
    public ShipOrderCommand ShipOrder(PaymentCharged e) => new(OrderId);

    public CancelReservationCommand CancelReservation() => new(OrderId);
    public RefundPaymentCommand RefundPayment() => new(OrderId);
}
```

Wiring (one line per saga):

```csharp
// AddSaga() implicitly calls AddMediator() in v1.1 — separate AddMediator()
// call is no longer needed, though it remains harmless (idempotent TryAdd*).
services.AddSaga()
    .AddOrderFulfillmentSaga();           // generator-emitted extension
```

That's it. Publish `OrderPlaced` via `IMediator.Publish` and the saga drives
itself: each `[Step]` runs in correlation-key order, returned commands flow
through `IMediator.Send`, downstream events advance the FSM, and a terminal
`Completed` (or `Compensated`) state removes the saga from the store
automatically.

## What's new in v1.1

- **`ISagaPersistableState`** + zero-allocation `SagaStateWriter` /
  `SagaStateReader` ref structs. Every `[Saga]` class implements the
  interface via a generator-emitted partial; backends use it to round-trip
  saga state across process boundaries. Supported state shapes: primitives,
  enums, `string`, `DateTime` / `DateTimeOffset` / `TimeSpan` / `Guid`,
  `[TypedId]`-attributed types, the common `record struct Foo(TPrim Bar)`
  shape, `byte[]`, and `Nullable<T>` wrappers thereof.
- **`[NotSagaState]`** escape-hatch attribute — exclude transient or
  computed members from generator-emitted Snapshot/Restore.
- **2 new diagnostics**:
  - `ZASAGA014` (Error) — saga state field has an unsupported type.
  - `ZASAGA015` (Info, suppressible) — saga commands should be idempotent
    under durable backends; fires when `WithEfCoreStore` / `WithRedisStore`
    is detected in the same compilation.
- **Implicit `AddMediator()`** — `AddSaga()` no longer requires a separate
  `services.AddMediator()` call.
- **InMemory backend unchanged** — v1.0 users see no behavioural change.
  Durable backends (starting with `ZeroAlloc.Saga.EfCore` 1.0.0) ship from
  the same repo in subsequent PRs.

## Documentation

- [`docs/concepts.md`](docs/concepts.md) — saga lifecycle, generator output, AOT story
- [`docs/correlation.md`](docs/correlation.md) — `[CorrelationKey]` rules, multi-saga subscription, composite keys
- [`docs/compensation.md`](docs/compensation.md) — `Compensate` / `CompensateOn`, reverse cascade, `ISagaManager.CompensateAsync`
- [`docs/diagnostics.md`](docs/diagnostics.md) — every `ZASAGA0XX` diagnostic with examples and fixes

## Samples

- [`samples/OrderFulfillment/`](samples/OrderFulfillment/) — full demo with happy path, compensation, orphan-event handling, and operator-initiated compensation. `dotnet run --project samples/OrderFulfillment/`.
- [`samples/AotSmoke/`](samples/AotSmoke/) — minimal saga end-to-end published with `PublishAot=true`. Run by the `aot-smoke` CI job on every push.

## Install

```bash
dotnet add package ZeroAlloc.Saga
```

The single package contains both the runtime and the source generator
(bundled as an analyzer asset). No separate `.Generator` package to install.

Hard dependencies pulled in transitively:

- `ZeroAlloc.Mediator` — for `INotification`, `IRequest`, `IMediator.Send`
- `Microsoft.Extensions.DependencyInjection` — for `AddSaga()`, `AddXxxSaga()`
- `Microsoft.Extensions.Logging.Abstractions` — for the saga-handler loggers

## Diagnostics

13 source-generator diagnostics catch authoring mistakes at compile time:

| ID | What | Severity |
|---|---|---|
| ZASAGA001 | `[Saga]` class not partial | error (code-fix: `Make partial`) |
| ZASAGA002 | Saga is static, abstract, generic, or nested | error |
| ZASAGA003 | Saga lacks parameterless ctor | error |
| ZASAGA004 | Step input event missing `[CorrelationKey]` | error |
| ZASAGA005 | `[CorrelationKey]` methods return inconsistent types | error |
| ZASAGA006 | `[CorrelationKey]` method has wrong signature | error |
| ZASAGA007 | `[Step(Order = ...)]` values have gaps or duplicates | error (code-fix: `Renumber steps`) |
| ZASAGA008 | `[Step]` method has wrong signature | error |
| ZASAGA009 | `[Step.Compensate]` target missing or mis-shaped | error (code-fix: `Add compensation method`) |
| ZASAGA010 | `[Step.CompensateOn]` event missing `[CorrelationKey]` | error |
| ZASAGA011 | `[CorrelationKey]` method appears to mutate state | warning |
| ZASAGA012 | `Compensate` without `CompensateOn` — dead code | warning |
| ZASAGA013 | Two sagas correlate on same event with different key types | warning |

Every diagnostic links to [`docs/diagnostics.md`](docs/diagnostics.md) with a
worked example.

## v1.0 known limitations

- **InMemory persistence is not durable.** Process crash loses all in-flight
  sagas. EfCore/Redis bridges arrive in v1.1.
- **`SagaLockManager` grows monotonically** — one `SemaphoreSlim` per unique
  correlation key seen, never evicted. Bounded by process lifetime; ~80 bytes
  each. Eviction lands in v1.x for high-cardinality workloads.
- **No timeouts.** v1.0 sagas wait indefinitely for the next event. Phase 4
  (v1.3) adds `[Step(TimeoutMs = ...)]` via Scheduling integration.
- **No telemetry.** v1.0 emits no spans, counters, or histograms. Phase 5
  (v1.4) ships `ZeroAlloc.Saga.Telemetry` bridge.

## Roadmap

| Phase | Package | Adds |
|---|---|---|
| **v1.0** (this release) | `ZeroAlloc.Saga` | runtime + generator + InMemory + diagnostics + AOT |
| v1.1 | `ZeroAlloc.Saga.EfCore`, `ZeroAlloc.Saga.Redis` | durable persistence, snapshot/rehydrate |
| v1.2 | `ZeroAlloc.Saga.Outbox`, `ZeroAlloc.Saga.Resilience` | transactional outbox, retry policies |
| v1.3 | (Scheduling integration) | per-step timeouts, deadlines |
| v1.4 | `ZeroAlloc.Saga.Telemetry`, `ZeroAlloc.Saga.Dashboard` | OTel spans/metrics, ops dashboard |
| v1.5 stretch | `ZeroAlloc.Saga.EventSourcing` | ES-backed store, choreography mode |

## License

MIT
