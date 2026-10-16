# Samples

Runnable projects under [`samples/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/). Each AOT sample is published with `PublishAot=true` in CI.

- [`samples/OrderFulfillment/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/OrderFulfillment/) — full demo with happy path, compensation, orphan-event handling, and operator-initiated compensation. `dotnet run --project samples/OrderFulfillment/` (InMemory) or `dotnet run --project samples/OrderFulfillment/ -- --efcore` (EfCore + SQLite).
- [`samples/AotSmoke/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/AotSmoke/) — minimal saga end-to-end published with `PublishAot=true`. Run by the `aot-smoke` CI job on every push.
- [`samples/AotSmokeEfCore/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/AotSmokeEfCore/) — EfCore variant of the smoke. Builds under the same AOT/trim analyzer set; runs end-to-end with SQLite in-memory verifying RowVersion rotation and Completed-state row removal.
- [`samples/AotSmokeOutbox/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/AotSmokeOutbox/) — the outbox bridge published with `PublishAot=true` and run in CI.
- [`samples/AotSmokeOrm/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/AotSmokeOrm/) — `ZeroAlloc.Saga.Orm` and `ZeroAlloc.Saga.Outbox.Orm` published with `PublishAot=true` and run in CI against a SQLite file.
- [`samples/AotSmokeRedis/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/AotSmokeRedis/) — `ZeroAlloc.Saga.Redis` and `ZeroAlloc.Saga.Outbox.Redis` published with `PublishAot=true` in CI; running it needs a Redis server, so CI publishes only.
- [`samples/ResilienceSample/`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/samples/ResilienceSample/) — `ZeroAlloc.Saga.Resilience` retrying a flaky receiver, published with `PublishAot=true` and run in CI.
