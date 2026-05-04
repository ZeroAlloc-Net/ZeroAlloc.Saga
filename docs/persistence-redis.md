# `ZeroAlloc.Saga.Redis` — durable persistence on Redis

Optional Redis-backed `ISagaStore<TSaga, TKey>` for `ZeroAlloc.Saga`. Mirrors
the shape of `ZeroAlloc.Saga.EfCore`: one fluent call swaps the InMemory
default for a Redis-backed store; OCC retries via the existing
scope-per-attempt loop; saga state round-trips as bytes via
`ISagaPersistableState` / `SagaStateReader` / `SagaStateWriter`.

> **Status:** ships in v1.x alongside `ZeroAlloc.Saga` 1.5+ and the new
> `ISagaUnitOfWork` abstraction. Requires `StackExchange.Redis` 2.8+.

## Storage shape

One Redis Hash per saga instance:

```text
saga:OrderFulfillmentSaga:42
  state    <bytes>            ; ISagaPersistableState.Snapshot()
  version  <Guid string>      ; rotated on every save
```

Final key shape: `{KeyPrefix}:{SagaTypeName}:{CorrelationKey.ToString()}`.
Default `KeyPrefix = "saga"` — override via `RedisSagaStoreOptions.KeyPrefix`.

The correlation key is rendered to a string via `key.ToString()`, matching
`EfCoreSagaStore`'s convention. For `[TypedId]`-attributed records, the
generator emits a clean `record.V` form; for ad-hoc `record struct OrderId(int V)`
the default `OrderId { V = 42 }` shape is functional but ugly — override
`ToString()` for human-readable Redis keys.

## OCC via WATCH / MULTI / EXEC

Save flow:

1. `WATCH` the saga key.
2. `HGET version` → compare to the version observed at the most recent
   `LoadOrCreateAsync`. If different, throw
   `RedisSagaConcurrencyException`.
3. `MULTI` → `HSET state=<new bytes> version=<new Guid>` → `EXEC`.
4. If `EXEC` returns `null` (a concurrent writer touched the watched key
   between step 2 and step 3), throw `RedisSagaConcurrencyException`.

The generator-emitted handler's `IsBackendConflict` method matches
`RedisSagaConcurrencyException` by fully-qualified name alongside EfCore's
`DbUpdateException` / `DbUpdateConcurrencyException`, so a Redis OCC clash
drives the same scope-per-attempt retry path as an EfCore conflict — every
attempt creates a fresh `IServiceScope`, fresh `RedisSagaStore`, fresh
observed-version map, and re-runs the load → fire → dispatch → save flow.

Per-key version tracking lives on the `RedisSagaStore` instance. A `null`
observed version means "the key did not exist when loaded" — the next save
treats it as an INSERT (still goes through `WATCH` + `MULTI` so a concurrent
INSERT race surfaces as `RedisSagaConcurrencyException`).

## Wiring

```csharp
// 1. Register IConnectionMultiplexer yourself (StackExchange.Redis convention).
services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("localhost:6379"));

// 2. AddSaga().WithRedisStore() flips the IsRedisBackend flag and registers
//    RedisSagaStore<TSaga, TKey> as Scoped. AddOrderFulfillmentSaga() then
//    picks up the registrar and wires per-saga state.
services.AddMediator();
services.AddSaga()
    .WithRedisStore(opts =>
    {
        opts.MaxRetryAttempts = 3;
        opts.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
        opts.UseExponentialBackoff = true;
        opts.KeyPrefix = "myapp:saga"; // optional, default "saga"
    })
    .WithOrderFulfillmentSaga();
```

`WithRedisStore()` is **mutually exclusive** with `WithEfCoreStore<TContext>()`.
Calling both throws `InvalidOperationException`.

## Composition with the outbox bridge

`WithRedisStore()` + `WithOutbox()` is **partially supported in this release**:

- `WithOutbox()` registers the default `OutboxStoreSagaUnitOfWork` (passthrough
  to `IOutboxStore.EnqueueDeferredAsync`). With an EfCore-backed `IOutboxStore`
  that's atomic via the shared `DbContext`. With a non-deferred Redis-backed
  outbox store, the `EnqueueAsync` fallback auto-commits — the dispatch row
  may exist in Redis even if the saga state save fails.
- The forthcoming **`ZeroAlloc.Saga.Outbox.Redis`** package (Stage 3) ships a
  Redis-native `RedisSagaUnitOfWork` that batches outbox writes into the
  saga store's `MULTI/EXEC`, restoring atomicity end-to-end. Until that
  ships, the `Saga.Redis` + `Saga.Outbox` combination has at-least-once
  dispatch semantics — step command handlers must be idempotent
  (`ZASAGA015`).

## `RedisSagaStoreOptions`

| Option | Default | Effect |
|---|---|---|
| `KeyPrefix` | `"saga"` | Prepended to every Redis key (`{prefix}:{type}:{key}`). |
| `MaxRetryAttempts` | 3 | Max OCC retries before re-throwing as `SagaConcurrencyException`. |
| `RetryBaseDelay` | 10 ms | Base wait between retries. |
| `UseExponentialBackoff` | `true` | Delay grows as `base * 2^(attempt-1)`. |

`RedisSagaStoreOptions` extends `SagaRetryOptions`, so the
generator-emitted handler reads the Redis-tuned retry knobs through the
backend-agnostic surface.

## AOT compatibility

The `ZeroAlloc.Saga.Redis` library is `<IsAotCompatible>true</IsAotCompatible>`
+ `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>` — builds clean under both
analyzers. `StackExchange.Redis` 2.8+ supports AOT per its release notes.

A truly `PublishAot=true` binary using the Redis backend has not been
empirically verified yet (the test suite uses Testcontainers.Redis under
JIT). A future `samples/AotSmokeRedis` mirroring `AotSmokeEfCore`'s
JIT-with-AOT-analyzers pattern can land in a follow-up.

## See also

- [`docs/persistence-efcore.md`](persistence-efcore.md) — sibling backend.
- [`docs/outbox.md`](outbox.md) — outbox-bridge composition rules.
- [`docs/diagnostics.md`](diagnostics.md) — full diagnostic catalog.
