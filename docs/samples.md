# Samples

Runnable projects under [`samples/`](../samples/). Each AOT sample is published with `PublishAot=true` in CI.

- [`samples/OrderFulfillment/`](../samples/OrderFulfillment/) — full demo with happy path, compensation, orphan-event handling, and operator-initiated compensation. `dotnet run --project samples/OrderFulfillment/` (InMemory) or `dotnet run --project samples/OrderFulfillment/ -- --efcore` (EfCore + SQLite).
- [`samples/AotSmoke/`](../samples/AotSmoke/) — minimal saga end-to-end published with `PublishAot=true`. Run by the `aot-smoke` CI job on every push.
- [`samples/AotSmokeEfCore/`](../samples/AotSmokeEfCore/) — EfCore variant of the smoke. Builds under the same AOT/trim analyzer set; runs end-to-end with SQLite in-memory verifying RowVersion rotation and Completed-state row removal.
- [`samples/AotSmokeOutbox/`](../samples/AotSmokeOutbox/) — the outbox bridge published with `PublishAot=true` and run in CI.
- [`samples/AotSmokeOrm/`](../samples/AotSmokeOrm/) — `ZeroAlloc.Saga.Orm` and `ZeroAlloc.Saga.Outbox.Orm` published with `PublishAot=true` and run in CI against a SQLite file.
- [`samples/AotSmokeRedis/`](../samples/AotSmokeRedis/) — `ZeroAlloc.Saga.Redis` and `ZeroAlloc.Saga.Outbox.Redis` published with `PublishAot=true` in CI; running it needs a Redis server, so CI publishes only.
- [`samples/ResilienceSample/`](../samples/ResilienceSample/) — `ZeroAlloc.Saga.Resilience` retrying a flaky receiver, published with `PublishAot=true` and run in CI.
