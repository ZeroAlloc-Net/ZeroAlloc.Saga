# `ZeroAlloc.Saga.Outbox.Redis` — atomic dispatch under Redis

Closes the cross-backend story for the Saga.Outbox bridge. Combined with
`ZeroAlloc.Saga.Redis`, every saga step's outbox-row write commits in the
same Redis `MULTI/EXEC` as the saga state save — so a failed save discards
both, and a successful retry produces exactly one outbox entry that the
poller dispatches exactly once.

> **Status:** ships in v1.x alongside `ZeroAlloc.Saga.Redis` and the
> `ISagaUnitOfWork` abstraction (Phase 3a-2 stage 1, PR #29). Requires
> `StackExchange.Redis` 2.8+.

## Architecture

Three pieces, all per-DI-scope:

1. **`RedisSagaUnitOfWork`** — the buffer the dispatcher enlists into. Replaces
   the default `OutboxStoreSagaUnitOfWork` (passthrough to `IOutboxStore.EnqueueDeferredAsync`)
   so the dispatch path no longer reaches `IOutboxStore` directly during the
   saga handler — the writes are deferred until the saga store opens its
   transaction.

2. **`IRedisSagaTransactionContributor`** — extension point on
   `ZeroAlloc.Saga.Redis`. `RedisSagaStore.SaveAsync` resolves all registered
   contributors at save time and calls `Contribute(transaction)` after queueing
   its own `HSET` for the saga state. The transaction is the one that's about
   to be `EXEC`-ed.

3. **`RedisOutboxTransactionContributor`** — the bridge. Drains the
   `RedisSagaUnitOfWork`'s buffer and queues the corresponding outbox-row
   `HSET` + `ZADD pending` commands on the saga store's transaction. `EXEC`
   commits saga + outbox together; if `EXEC` aborts (WATCH detected change),
   both the saga update and the outbox writes are discarded.

Plus **`RedisOutboxStore`** — a backend-agnostic `IOutboxStore` that the
`OutboxSagaCommandPoller` reads from to drain pending entries and dispatch
them. Storage shape:

```text
{KeyPrefix}:entry:{id}    Hash    typeName, payload, retryCount, status, createdAt, ...
{KeyPrefix}:pending       SortedSet  score = next-retry tick, member = id
{KeyPrefix}:succeeded     Set
{KeyPrefix}:deadletter    Set
```

## Wiring

```csharp
services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("localhost:6379"));

services.AddSaga()
    .WithRedisStore(opts => opts.KeyPrefix = "myapp:saga")
    .WithOutbox()                                    // <-- registers OutboxSagaCommandDispatcher
    .WithRedisOutbox(opts => opts.KeyPrefix = "myapp:saga-outbox")  // <-- closes the atomicity loop
    .WithOrderFulfillmentSaga();
```

Order matters: call `WithRedisOutbox` AFTER both `WithRedisStore` and
`WithOutbox`. `WithRedisOutbox` registers `RedisSagaUnitOfWork` as the
canonical `ISagaUnitOfWork` (overriding the default passthrough that
`WithOutbox` registered), and replaces `IOutboxStore` with `RedisOutboxStore`
so the poller reads from the same Redis key-space the saga store writes to.

## Atomicity contract

For every saga step:

1. Saga handler calls `dispatcher.DispatchAsync(cmd, ct)`.
   - The dispatcher (`OutboxSagaCommandDispatcher`) resolves
     `ISagaUnitOfWork` from the current scope — this is now the
     `RedisSagaUnitOfWork`. The cmd is serialized via `ISerializer<T>` and
     the bytes go into the per-scope buffer.
2. Saga handler calls `_store.SaveAsync(...)`.
   - `RedisSagaStore.SaveAsync` opens `WATCH` on the saga key, version-checks,
     opens a `MULTI` batch with the saga state `HSET`, then iterates registered
     `IRedisSagaTransactionContributor`s. The `RedisOutboxTransactionContributor`
     drains the unit of work's buffer and queues `HSET` + `ZADD pending` for
     each enlisted entry.
   - `EXEC` commits everything atomically. On WATCH-conflict, `EXEC` returns
     null and `RedisSagaConcurrencyException` propagates, triggering the
     scope-per-attempt retry loop. The next attempt's scope is fresh — fresh
     `RedisSagaUnitOfWork` (empty buffer), fresh saga state — so the previous
     attempt's outbox writes are discarded. Same atomicity contract as the
     `Saga.EfCore + Saga.Outbox.EfCore` shape, just with Redis primitives.

## Cross-process race

Each replica has its own scope and its own `RedisSagaUnitOfWork`. Two replicas
processing the same correlation key both `WATCH` the saga key, both build
their `MULTI` batches; the first `EXEC` wins, the second sees the watched
key changed and throws. Loser's outbox writes are never persisted.

## Poller integration

`OutboxSagaCommandPoller` (from `ZeroAlloc.Saga.Outbox`) is unchanged. It
reads `IOutboxStore.FetchPendingAsync` — under `WithRedisOutbox`, that's the
`RedisOutboxStore` reading the same `{KeyPrefix}:pending` sorted set the
contributor wrote to. Per-entry retry / dead-letter via `MarkFailedAsync` /
`DeadLetterAsync` go through the same `RedisOutboxStore`.

## Limitations

- **`RedisOutboxStore.EnqueueDeferredAsync` throws.** This is by design: the
  Redis bridge's atomic-dispatch path goes through `RedisSagaUnitOfWork`, NOT
  through `IOutboxStore.EnqueueDeferredAsync`. Throwing makes
  misconfiguration loud (`OutboxSagaCommandDispatcher` shouldn't reach this
  method when `WithRedisOutbox` is correctly registered).
- **No StreamReadGroup-style consumer-group routing.** The `RedisOutboxStore`
  uses Hash + SortedSet for poller compatibility. A future variant could ship
  with `XADD` streams + consumer groups for higher-throughput dispatch fan-out.

## See also

- [`docs/persistence-redis.md`](persistence-redis.md) — `Saga.Redis` backend.
- [`docs/outbox.md`](outbox.md) — base outbox-bridge shape.
- [`docs/persistence-efcore.md`](persistence-efcore.md) — sibling EfCore backend.
