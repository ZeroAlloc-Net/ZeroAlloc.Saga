# `ZeroAlloc.Saga.Outbox.Redis` — atomic dispatch under Redis

Closes the cross-backend story for the Saga.Outbox bridge. Combined with
`ZeroAlloc.Saga.Redis`, every saga step's outbox-row write commits in the
same Redis `MULTI/EXEC` as the saga state save — so a failed save discards
both, and a successful retry produces exactly one outbox entry that
ZeroAlloc.Outbox's worker dispatches exactly once.

> **Status:** 4.0 implements the ZeroAlloc.Outbox 3.0 lease contract. Requires
> `ZeroAlloc.Outbox` 3.0.1 or later, not 3.0.0 — see [`docs/outbox.md`](outbox.md) for why —
> and `StackExchange.Redis` 2.8+. EF Core users also need `ZeroAlloc.Outbox.EfCore` 3.0.1 or
> later; Saga's floor on `ZeroAlloc.Outbox` does not raise it. Upgrading from 3.x? See
> [Migrating to v4](migrating-to-v4.md).

## Architecture

Three pieces, all per-DI-scope:

1. **`RedisSagaUnitOfWork`** — the buffer the dispatcher enlists into. Replaces
   the default `OutboxStoreSagaUnitOfWork` (passthrough to `IOutboxStore.EnqueueDeferredAsync`)
   so the dispatch path no longer reaches `IOutboxStore` directly during the
   saga handler — the writes are deferred until the saga store opens its
   transaction.

2. **`IRedisSagaTransactionContributor`** — extension point on
   `ZeroAlloc.Saga.Redis`. `RedisSagaStore.SaveAsync` and `RemoveAsync` both call
   `Contribute(transaction)` on every registered contributor after queueing their
   own write: the `HSET` for the saga state, or the `DEL` of the saga key. The
   transaction is the one that's about to be `EXEC`-ed.

3. **`RedisOutboxTransactionContributor`** — the bridge. Drains the
   `RedisSagaUnitOfWork`'s buffer and queues the corresponding outbox-row
   `HSET` + `ZADD pending` commands on the saga store's transaction. `EXEC`
   commits saga + outbox together; if `EXEC` aborts (WATCH detected change),
   both the saga update and the outbox writes are discarded.

Plus **`RedisOutboxStore`** — the `IOutboxStore` that ZeroAlloc.Outbox's `OutboxWorkerService`
claims pending entries from. Storage shape:

```text
{KeyPrefix}:entry:{id}    Hash       typeName, payload, retryCount, status, createdAt,
                                     nextRetryAt, processedAt, error, lockedBy, lockedUntil
{KeyPrefix}:pending       SortedSet  score = lease expiry while leased, else due time; member = id
{KeyPrefix}:succeeded     Set
{KeyPrefix}:deadletter    Set
```

## Wiring

```csharp
services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect("localhost:6379"));

// ZeroAlloc.Outbox's worker dispatches the saga commands. Call AddOutbox() exactly once;
// WithRedisOutbox() below supplies its IOutboxStore.
services.AddOutbox(o => o.LeaseDuration = TimeSpan.FromMinutes(2));

services.AddSaga()
    .WithRedisStore(opts => opts.KeyPrefix = "myapp:saga")
    .WithOutbox()                                    // <-- registers OutboxSagaCommandDispatcher + saga dispatchers
    .WithRedisOutbox(opts => opts.KeyPrefix = "myapp:saga-outbox")  // <-- closes the atomicity loop
    .WithOrderFulfillmentSaga();
```

Order matters within the saga builder: call `WithRedisOutbox` AFTER both `WithRedisStore` and
`WithOutbox`. `WithRedisOutbox` registers `RedisSagaUnitOfWork` as the canonical `ISagaUnitOfWork`
(overriding the default passthrough that `WithOutbox` registered), and replaces `IOutboxStore`
with `RedisOutboxStore`, so the worker claims from the same Redis key-space the saga store
writes to. `AddOutbox()` can come before or after `AddSaga()`. The options, the
one-dispatcher-per-type-name rule and the startup check are described in
[`docs/outbox.md`](outbox.md).

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
   - On the step that completes the saga, and when compensation finishes, the
     handler calls `_store.RemoveAsync(...)` instead. It runs the same way with a
     `DEL` of the saga key in place of the `HSET`, so the last step's and the
     compensation commands commit with the removal. It does so even when the key
     does not exist, as for a saga that one event both starts and completes.

## Cross-process race

Each replica has its own scope and its own `RedisSagaUnitOfWork`. Two replicas
processing the same correlation key both `WATCH` the saga key, both build
their `MULTI` batches; the first `EXEC` wins, the second sees the watched
key changed and throws. Loser's outbox writes are never persisted.

## Dispatch: claims and leases

ZeroAlloc.Outbox's worker drives `RedisOutboxStore` through the 3.0 lease contract. Each
operation is one Lua script, so it runs atomically on the Redis server. StackExchange.Redis sends
a script's first call as `EVAL` together with `SCRIPT LOAD`, sends `EVALSHA` afterwards, and falls
back to `EVAL` and reloads it when the server answers `NOSCRIPT` — for example after a restart or
`SCRIPT FLUSH`.

- **Claim.** `ZRANGEBYSCORE pending -inf now` returns up to a batch of due ids. For each one whose
  status is `Pending` — or the legacy `Failed`, see [Migrating to v4](migrating-to-v4.md) — the
  script sets `lockedBy` and `lockedUntil` and moves the id's score to `lockedUntil`. That hides it
  from every other claim until the lease runs out, and an entry whose host died is claimable again
  once it has, with no sweeper. An id whose hash is missing or no longer pending is removed from
  the set.
- **Renew.** Right before dispatching, the worker extends the lease, but only while this host
  still holds an unexpired one. If it no longer does, the worker skips the entry.
- **Marks.** Succeeded, failed and dead-lettered each apply only while the entry is pending and
  this host is its `lockedBy`, and they clear the lease. A host that lost its lease cannot
  overwrite another host's outcome. A failed attempt keeps the entry pending and moves its score
  to the next retry time.
- **Release.** A worker that stops mid-batch clears its leases on the entries it did not finish,
  making them due at once.

Every script takes the current time from the calling host's clock, as the other ZeroAlloc.Outbox
stores do, so hosts need roughly synchronised clocks. See ZeroAlloc.Outbox's
[claim, renew and release model](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/outbox-pattern.md#the-claim-renew-and-release-model).

Every key a script touches comes from `KEYS`, not `ARGV` — the claim receives the entry-key prefix
as a key and appends each id to it inside the script, rather than building a full key on the
client. That makes a key-prefixed `IDatabase` from StackExchange.Redis's `WithKeyPrefix` work
correctly: the prefix is applied to `KEYS` before the script runs.

## Redis Cluster

Redis Cluster is not supported, in 3.x or 4.0. The claim script reads and writes entry hashes whose keys it builds from the entry-key prefix and each id, because it cannot know the ids before it runs. Redis requires a script to declare every key it accesses in `KEYS`, so this is outside Redis's scripting contract even when the keys share a hash slot, and some Redis-compatible servers reject it. With `WithRedisOutbox()`, the saga store's `MULTI/EXEC` also writes the saga key and the outbox keys in one transaction. The test suite runs against a single Redis instance. Run the store against a single primary.

## Limitations

- **`RedisOutboxStore.EnqueueDeferredAsync` throws.** This is by design: the
  Redis bridge's atomic-dispatch path goes through `RedisSagaUnitOfWork`, NOT
  through `IOutboxStore.EnqueueDeferredAsync`. Throwing makes
  misconfiguration loud (`OutboxSagaCommandDispatcher` shouldn't reach this
  method when `WithRedisOutbox` is correctly registered).
- **No StreamReadGroup-style consumer-group routing.** The `RedisOutboxStore`
  uses Hash + SortedSet. A future variant could ship with `XADD` streams +
  consumer groups for higher-throughput dispatch fan-out.

## See also

- [`docs/migrating-to-v4.md`](migrating-to-v4.md) — upgrading from Saga 3.x, including Redis data.
- [`docs/persistence-redis.md`](persistence-redis.md) — `Saga.Redis` backend.
- [`docs/outbox.md`](outbox.md) — base outbox-bridge shape and dispatch options.
- [`docs/persistence-efcore.md`](persistence-efcore.md) — sibling EfCore backend.
