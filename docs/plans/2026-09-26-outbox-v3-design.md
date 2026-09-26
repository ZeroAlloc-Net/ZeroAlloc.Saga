# Saga on Outbox 3.0: design

**Issue:** #173. **Release:** ZeroAlloc.Saga 4.0.0, a major. Every Saga package releases in lockstep. **Status:** approved 2026-09-26.

## Problem

ZeroAlloc.Outbox 3.0.0 (ZeroAlloc-Net/ZeroAlloc.Outbox#201) replaced `IOutboxStore.FetchPendingAsync` with a lease-based atomic claim, so that concurrent hosts never dispatch the same message twice. Saga has three problems with that change.

1. **The Redis store doesn't compile against 3.0, and it has no claim.**
   - `RedisOutboxStore` implements the 2.x `IOutboxStore`.
   - Its fetch is a plain `ZRANGEBYSCORE` followed by one `HMGET` per id, so every replica sees and dispatches the same commands.
   - Its marks are unconditional `MULTI` blocks, so a late `MarkFailedAsync` can put a finished command back into pending.
2. **Saga's own poller has no claim.** `OutboxSagaCommandPoller` runs its own fetch → dispatch → mark loop with none of the 3.0 lease semantics: claim, renew, release, and "completed elsewhere".
3. **The documented EF setup runs two pollers on one table.**
   - The documented setup is `docs/outbox.md`: `services.AddOutbox().WithEfCore<AppDbContext>()` plus `AddSaga()…WithOutbox()`.
   - `AddOutbox()` registers Outbox's `OutboxWorkerService`, and `WithOutbox()` registers `OutboxSagaCommandPoller`.
   - The worker has no `IOutboxTypeDispatcher` for saga command types, so a saga row it claims first is dead-lettered as "No dispatcher for type …". Under 3.0 the two pollers would also contend for leases while sharing the per-process default `HostId`.

## Why Saga had its own poller

Saga commands are not `[OutboxMessage]` types. They are dispatched through the generator-emitted, internal `ZeroAlloc.Saga.Generated.SagaCommandRegistry.DispatchAsync(string typeName, ReadOnlyMemory<byte> payload, IServiceProvider sp, IMediator mediator, CancellationToken ct)`. `WithOutbox()` locates that method reflectively, with AOT roots and suppressions. No `IOutboxTypeDispatcher` existed for these types, so Outbox's worker could not dispatch them.

## Decision: one poller, Outbox's worker

Retire `OutboxSagaCommandPoller`. Saga registers one `IOutboxTypeDispatcher` for each saga command type, and Outbox's worker dispatches saga commands like any other outbox message.

Saga commands get everything the 3.0 worker does:
- claim, per-message renew, lease-guarded marks;
- release on stop or error;
- outcome recording that survives shutdown;
- exponential retry, dead-lettering, telemetry and dashboard events.

The two-poller race disappears because only one poller remains.

**Rejected:** porting the 3.0 loop into Saga's poller. It duplicates the worker's subtle lease logic in a second place, and it needs a separate fix for the two-poller race.

## Design

### Dispatch

- **Dispatcher registration.** `WithOutbox()` registers, for each saga command type name, a **scoped** `IOutboxTypeDispatcher` whose `TypeName` is that name. Its `DispatchAsync(payload, ct)` calls `SagaCommandRegistry.DispatchAsync(typeName, payload, sp, mediator, ct)`.
  - `sp` is the dispatcher's own constructor-injected scoped `IServiceProvider`.
  - `IMediator` is resolved from `sp`.
  - One small internal dispatcher class, parameterised by type name, is enough.
- **Scope sharing.** Outbox's worker resolves the store and all dispatchers from the same per-batch scope, so saga dispatch shares the store's scope, and its DbContext on the EF path, exactly as the old poller did.
- **The type-name list.** The generator emits a static list of the registry's type names on `SagaCommandRegistry`. `WithOutbox()` reads it the same way it already locates `DispatchAsync`, with the same AOT rooting via `DynamicDependency`. The names are exactly the strings the unit of work writes as `OutboxEntry.TypeName`.
- **Duplicate type names.** If another registered `IOutboxTypeDispatcher` claims a saga command's type name, the worker's registry keeps the last one. `WithOutbox()` must check at startup and throw on a conflict, naming the type.

### Registration and options

- **What's removed.** `OutboxSagaCommandPoller` and `OutboxSagaPollerOptions` are removed from the public API.
- **Configuration moves to Outbox.** Users configure dispatch through `AddOutbox(o => …)`: `PollingInterval`, `BatchSize`, `MaxAttempts`, `RetryBaseDelay`, `LeaseDuration` and `HostId`.
- **How the old options map:**
  - `PollInterval` → `PollingInterval`
  - `BatchSize` → `BatchSize`
  - `MaxRetries` → `MaxAttempts`
  - `RetryDelay`, which was fixed, → `RetryBaseDelay`, which is exponential.
- **`WithOutbox()` does not call `AddOutbox()`.** Outbox registers its worker with a plain `AddHostedService`, so a second `AddOutbox()` would start a second worker (ZeroAlloc-Net/ZeroAlloc.Outbox#206).
  - At startup, `WithOutbox()` requires that an `OutboxWorkerService` hosted service and an `IOutboxStore` are registered. If either is missing, it throws with a message showing the supported setup.
  - The check runs when the container is built, or through a hosted-service start check. It must not fail at registration time, because the order of `AddOutbox` and `AddSaga` is the user's choice.
- **The supported setups:**
  - EF: `services.AddOutbox(o => …).WithEfCore<AppDbContext>(); services.AddSaga()…WithOutbox();`
  - Redis: `services.AddOutbox(o => …); services.AddSaga()…WithRedisStore()…WithOutbox().WithRedisOutbox();`, where `WithRedisOutbox` supplies the `IOutboxStore`, as it does today.

### Redis store on the 3.0 contract

Each operation is one Lua script, loaded once with `LuaScript.Prepare` and `Load` and called with `EVALSHA`, so it runs atomically on the Redis server. Keys stay as they are today: `{P}:entry:{id}` hashes, the `{P}:pending` sorted set, and the `{P}:succeeded` and `{P}:deadletter` sets. The hash gains the fields `lockedBy` and `lockedUntil` (unix ms).

- **`ClaimPendingAsync(batchSize, lease)`.** The script runs `ZRANGEBYSCORE pending -inf now LIMIT 0 batchSize`.
  - For each id whose status is `Pending` (or the legacy `Failed`; see Migration), it:
    - sets `status=Pending`, `lockedBy=host` and `lockedUntil=now+duration`;
    - **re-scores** the id to `lockedUntil`;
    - returns `typeName`, `payload`, `retryCount` and `createdAt`.
  - Re-scoring hides a leased entry from other claims until the lease expires. An expired lease is then due again automatically, with no sweeper.
  - An id whose hash is missing or not pending is `ZREM`-ed as garbage.
  - One round trip replaces today's N+1.
- **`RenewLeaseAsync(id, lease)`.** Compare-and-set on `status == Pending AND lockedBy == host AND lockedUntil >= now`. It extends `lockedUntil` and re-scores the id. Returns whether it renewed.
- **`MarkSucceededAsync(id, lease)`, `MarkFailedAsync(id, retryCount, nextRetryAt, lease)` and `DeadLetterAsync(id, error, lease)`.** Each is a compare-and-set on `status == Pending AND lockedBy == host`, with no expiry check, matching the 3.0 contract. Each clears the lease and returns `bool`.
  - Succeeded and dead-letter use `ZREM pending` plus `SADD` to their set.
  - Failed keeps the status `Pending`, sets `retryCount`, and re-scores to `nextRetryAt`.
- **`ReleaseLeasesAsync(ids, lease)`.** For each id, a compare-and-set on `lockedBy == host AND status == Pending`. It clears the lease and re-scores to `now`. Returns the number released.
- **The clock.** Every script takes `now` as an argument from the client clock, which is what the other 3.0 stores use. It does not use Redis `TIME`, so the Redis store behaves like the other stores. Hosts need roughly synchronised clocks, as the Outbox docs already state.
- **Enqueue** (`EnqueueAsync` and `RedisOutboxTransactionContributor`) is unchanged, except that it initialises no lease fields; absent means unleased.

### Migration of existing Redis data

- Entries written by 2.x have no `lockedBy` or `lockedUntil`, which means unleased, so they are claimable as they are.
- 2.x marked retried entries `status=Failed` while leaving them in `pending`. The claim script treats `Failed` as `Pending`, rewriting it on claim, so in-flight retries survive the upgrade. The v4 migration guide documents this. No data migration step is needed.

### Other implementers

- **`samples/AotSmokeOutbox/InProcessOutboxStore`** moves to the 3.0 shape, or uses Outbox's InMemory store if that fits the sample.
- **Test fakes.** The poller-test `FakeOutboxStore` goes away with the poller. `CapturingOutboxStore` in `OutboxStoreSagaUnitOfWorkTests` updates its signatures.

## Public API (breaking)

- **Removed:** `OutboxSagaCommandPoller` (constructor and `PollOnceAsync`), and `OutboxSagaPollerOptions` with all its members.
- **Changed:** `RedisOutboxStore` implements the 3.0 `IOutboxStore`:
  - `FetchPendingAsync` is removed;
  - `ClaimPendingAsync`, `RenewLeaseAsync` and `ReleaseLeasesAsync` are added;
  - the marks take `OutboxLease` and return `ValueTask<bool>`.
- **Dependency floors:** `ZeroAlloc.Outbox` and `ZeroAlloc.Outbox.EfCore` (tests) go to 3.0.0.
- **Bookkeeping:** PublicAPI files get `*REMOVED*` lines plus new entries. Root `apicompat-suppressions.xml` gets CP0002/CP0006 entries for net8.0 and net10.0 with justifications that cite #173.

## Tests

- **The two-poller regression, EF.** With the documented setup, several saga commands are all dispatched through the worker, and none is dead-lettered as "No dispatcher".
- **Dispatch scope.** A saga command dispatched through the worker sees the same scoped DbContext as the store.
- **Startup checks.** `WithOutbox()` without `AddOutbox()` throws a clear message. So does a duplicate type-name dispatcher.
- **Redis concurrency,** with Testcontainers Redis:
  - N claimers over M commands: each command is claimed exactly once.
  - An expired lease is re-claimed.
  - Renew fails after another host has claimed.
  - A late mark returns false and doesn't overwrite the result.
  - Release makes an entry immediately claimable.
  - A 2.x-shaped `Failed` entry is claimed after the upgrade.
- **End to end.** The existing EF and Redis E2E tests move to `AddOutbox()` plus the worker. The OCC rollback and WATCH-conflict tests keep passing.
- **Builds and warnings.** The AOT smoke sample builds and runs under ILC with no new trim warnings. Every project builds with 0 warnings and no new suppressions.

## Docs

- `docs/outbox.md` and `docs/outbox-redis.md`: the supported setups, the options mapping, the lease model and its Outbox docs link, and the duplicate type-name rule.
- A new `docs/migrating-to-v4.md` covering:
  - removed types;
  - the options mapping, including that retry is now exponential;
  - calling `AddOutbox()` explicitly;
  - the EF `AddOutboxLease` migration, from Outbox's guide;
  - Redis data compatibility;
  - the fixed two-poller race.

## Delivery

- One PR closes #173. The commit is `feat!:` with a `BREAKING CHANGE:` footer.
- The PR body carries a `BEGIN_COMMIT_OVERRIDE` block listing:
  - the `feat!:`;
  - `fix:` the documented EF setup dead-lettered saga commands;
  - `fix:` the Redis outbox dispatched each command on every replica.
- Commit bodies have lines of at most 100 characters and no nested parentheses.
- After the merge, confirm the release PR proposes 4.0.0 and lists the entries.
