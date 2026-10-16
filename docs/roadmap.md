# Limitations and roadmap

## Known limitations

- **InMemory persistence is not durable.** Process crash loses all in-flight
  sagas. Switch to `ZeroAlloc.Saga.EfCore`, `ZeroAlloc.Saga.Orm` or
  `ZeroAlloc.Saga.Redis` for durability.
- **`ZeroAlloc.Saga.EfCore` Native AOT publish** — the runtime library
  itself is AOT-clean, but a fully `PublishAot=true` binary is blocked
  upstream by EF Core's experimental NativeAOT support (precompiled queries
  don't yet cover the store's tracked `Set<>().AsTracking()...` shape).
  Use the EfCore backend on JITted hosts, or the ORM or Redis store, whose
  smoke samples publish with `PublishAot=true` in CI. Tracked in
  [#16](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/16). See
  [`docs/persistence-efcore.md`](persistence-efcore.md).
- **No step timeouts.** A saga waits indefinitely for its next event; there
  is no per-step timeout or deadline.
- **No built-in telemetry.** The saga runtime emits log entries but no
  OpenTelemetry spans or metrics.

## Roadmap

Shipped, all at the current 4.x version:

| Package | Provides |
|---|---|
| `ZeroAlloc.Saga` | runtime + generator + InMemory store + diagnostics + AOT, `ISagaCommandDispatcher` indirection, snapshot/rehydrate via `ISagaPersistableState` |
| `ZeroAlloc.Saga.EfCore`, `ZeroAlloc.Saga.Orm`, `ZeroAlloc.Saga.Redis` | durable persistence with retry-on-OCC-conflict |
| `ZeroAlloc.Saga.Outbox`, `.Outbox.Orm`, `.Outbox.Redis` | atomic command dispatch through the transactional outbox |
| `ZeroAlloc.Saga.Resilience` | retry / timeout / circuit-breaker / rate-limit around step dispatch |

Not yet started, no package exists:

- per-step timeouts and deadlines, through ZeroAlloc.Scheduling
- OpenTelemetry spans and metrics, and an operations dashboard
- an event-sourced store and a choreography mode
