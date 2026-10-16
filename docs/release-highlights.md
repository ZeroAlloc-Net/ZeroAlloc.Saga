# Release highlights

The narrative "what's new" notes that used to sit in the README, kept here as written. The
complete, per-release list of changes is in [CHANGELOG.md](../CHANGELOG.md); upgrade steps for
4.0 are in [Migrating to v4](migrating-to-v4.md).

## Status at 4.x

> **Status:** AOT compatible. The generator-emitted saga handler runs
> each OCC retry attempt in a fresh `IServiceScope`, and the
> `ZeroAlloc.Saga.Outbox` bridge commits every step command's dispatch
> row atomically with the saga state save on the EF Core store, and on
> the Redis store with `WithRedisOutbox()`, and on the ORM store with
> `WithOrmOutbox()` — together they eliminate Saga 1.1's "OCC retry can
> dispatch twice" caveat for both cross-process races and same-process
> retries.
> Durable persistence via `ZeroAlloc.Saga.EfCore` (single shared `SagaInstance` table,
> row-version OCC, retry-on-conflict) is unchanged. InMemory remains
> the default backend; switch to EfCore with one fluent call, and opt
> into the outbox bridge with `.WithOutbox()`. See
> [`docs/persistence-efcore.md`](persistence-efcore.md) and
> [`docs/outbox.md`](outbox.md).

## 4.x

### `ZeroAlloc.Saga.Outbox.Orm` (new package)

Atomic outbox dispatch on the ZeroAlloc.ORM saga store. A saga step's outbox rows are written in
the same database transaction as the saga row, through ZeroAlloc.Outbox.Orm, so a failed save or
removal discards both.

```csharp
services.AddOutbox().WithOrm(OutboxOrmDialect.SqlServer);
services.AddSaga()
    .WithOrmStore()
    .WithOutbox()
    .WithOrmOutbox()          // <-- commits the outbox rows in the saga store's transaction
    .WithOrderFulfillmentSaga();
```

Requires ZeroAlloc.Outbox 4.4.3 or later. See
[`docs/outbox.md`](outbox.md#zeroallocsagaorm-atomic-with-withormoutbox).

### `ZeroAlloc.Saga.Outbox.Redis` (new package — closes Phase 3a-2)

Redis-native atomic dispatch. Saga state save and outbox-row write commit
together in a single Redis `MULTI/EXEC`, so a failed save discards both —
matching the `Saga.EfCore + Saga.Outbox.EfCore` story for Redis-backed
sagas.

```csharp
services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("..."));
services.AddOutbox();         // ZeroAlloc.Outbox's worker dispatches the saga commands
services.AddSaga()
    .WithRedisStore()
    .WithOutbox()
    .WithRedisOutbox()        // <-- enlists outbox writes into the saga store's MULTI/EXEC
    .WithOrderFulfillmentSaga();
```

See [`docs/outbox-redis.md`](outbox-redis.md) for the full atomicity
contract, the `IRedisSagaTransactionContributor` extension point, and how dispatch claims entries
under a lease.

### `ZeroAlloc.Saga.Redis` (new package)

Second durable backend, mirroring `Saga.EfCore`. One Redis Hash per saga
instance with `state` (bytes) + `version` (Guid) fields; OCC via
`WATCH/MULTI/EXEC`; conflicts surface as `RedisSagaConcurrencyException`
which the generator-emitted handler's retry loop catches alongside EfCore's
exceptions.

```csharp
services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("..."));
services.AddSaga()
    .WithRedisStore(opts => opts.KeyPrefix = "myapp:saga")
    .WithOrderFulfillmentSaga();
```

Mutually exclusive with `WithEfCoreStore<TContext>()`. Composition with
`WithOutbox()` alone is at best at-least-once, and fails at startup with an EF Core outbox store;
add `ZeroAlloc.Saga.Outbox.Redis` and `WithRedisOutbox()` for full atomic-commit guarantees,
see above. Requires `StackExchange.Redis` 3.4 or later. See [`docs/persistence-redis.md`](persistence-redis.md).

### `ISagaUnitOfWork` abstraction (Phase 3a-2 stage 1)

The dispatch-side outbox enlistment now goes through `ISagaUnitOfWork`
instead of `IOutboxStore` directly, opening the door for backend-specific
unit-of-work implementations (Redis MULTI/EXEC, etc.). No behavior change
for existing EfCore + Outbox consumers — the default
`OutboxStoreSagaUnitOfWork` is a passthrough.

### Builder API: `Add{Saga}Saga()` → `With{Saga}Saga()`

The generator-emitted per-saga registration method is renamed from
`Add{Saga}Saga()` to `With{Saga}Saga()` so it aligns with the rest of
the builder API (`WithEfCoreStore`, `WithOutbox`, `WithResilience`).
The legacy `Add`-prefixed shim, which emitted diagnostic `ZASAGA018`,
was removed in 4.0.

```csharp
services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .WithOrderFulfillmentSaga();
```

### `ZeroAlloc.Saga.Resilience` (new package)

Optional bridge that wraps every saga step command's dispatch in a
`ZeroAlloc.Resilience` policy stack — retry, timeout, circuit-breaker,
rate-limit. One fluent call configures the pipeline:

```csharp
services.AddSaga()
    .WithEfCoreStore<AppDbContext>(opts => opts.MaxRetryAttempts = 3)
    .WithOrderFulfillmentSaga()
    .WithResilience(r =>
    {
        r.Retry = new RetryPolicy(maxAttempts: 5, backoffMs: 200, jitter: true, perAttemptTimeoutMs: 5_000);
        r.CircuitBreaker = new CircuitBreakerPolicy(maxFailures: 10, resetMs: 30_000, halfOpenProbes: 1);
    });
```

Composition order is outermost-first: `circuit-breaker → rate-limit →
timeout → retry → inner.DispatchAsync`. Caller cancellation propagates
as `OperationCanceledException`; policy denials surface as
`ResilienceException(Policy: ...)` so consumers can disambiguate.
Requires `ZeroAlloc.Resilience` 3.3 or later. See [`docs/resilience.md`](resilience.md).

### `ZeroAlloc.Saga.Outbox` (new package)

Optional bridge that routes every saga step command through
`ZeroAlloc.Outbox` so dispatch is committed in the same database
transaction as the saga state save. Eliminates the cross-process race
where a state update can succeed without the corresponding command being
delivered.

```csharp
services.AddOutbox().WithEfCore<AppDbContext>();   // the worker that dispatches saga commands
services.AddSaga()
    .WithEfCoreStore<AppDbContext>(opts => opts.MaxRetryAttempts = 3)
    .WithOutbox()                        // <-- one fluent call
    .WithOrderFulfillmentSaga();
```

Requires `ZeroAlloc.Outbox` 4.4.3+ and `ZeroAlloc.Serialisation` 2.8.1+. EF Core users also need
the matching 4.x `ZeroAlloc.Outbox.EfCore`; Saga's floor on `ZeroAlloc.Outbox` does not raise it.
See [`docs/outbox.md`](outbox.md) for the full setup, the
`ISerializer<T>` each step command needs, `ZASAGA017`, and dispatch options. Upgrading from 3.x: see
[`docs/migrating-to-v4.md`](migrating-to-v4.md).

### `ZeroAlloc.Saga` runtime

- **The generated `Snapshot()` allocates only the array it returns.** It writes into a scratch
  writer each thread keeps and reuses, and returns an exact-size `byte[]`. For the test saga's
  21-byte state that is 48 B per call, down from 168 B, asserted by `SagaStateAllocationTests`
  ([#273](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/273)). Every durable store
  calls it once per saga step.
- **`ISagaCommandDispatcher`** indirection: step handlers no longer
  depend on `IMediator` directly. Default impl (generator-emitted)
  forwards to `IMediator.Send`; `Saga.Outbox`'s `WithOutbox()` swaps
  it in for transactional dispatch.
- **`SagaCommandRegistry`** generator-emitted in consumer assemblies —
  central deserialise+dispatch lookup keyed by `typeof(T).FullName`,
  resolves `ISerializer<T>` from DI.
- **`ZASAGA016` / `ZASAGA017`** new diagnostics, and an auto-`[ZeroAllocSerializable]`
  partial extension on step command types. The extension and `ZASAGA016` were later
  removed, because ZeroAlloc.Serialisation's generator never saw the generated attribute
  ([#207](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/207)). Apply
  `[ZeroAllocSerializable]` or register an `ISerializer<T>` for each outbox step command.

## Earlier releases: durable persistence

### `ZeroAlloc.Saga` 1.2.0

- **`ISagaPersistableState`** + `SagaStateWriter` / `SagaStateReader` ref
  structs. The writer writes into your `IBufferWriter<byte>` and allocates
  0 B; the reader allocates 0 B apart from the `string` and `byte[]` values
  it returns. The generated `Snapshot()` returns a new `byte[]` per call.
  Every `[Saga]` class implements the
  interface via a generator-emitted partial; backends use it to round-trip
  saga state across process boundaries. Supported state shapes: primitives,
  enums, `string`, `DateTime` / `DateTimeOffset` / `TimeSpan` / `Guid`,
  `[TypedId]`-attributed types, the common `record struct Foo(TPrim Bar)`
  shape, `byte[]`, and `Nullable<T>` wrappers thereof.
- **`[NotSagaState]`** escape-hatch attribute — exclude transient or
  computed members from generator-emitted Snapshot/Restore.
- **`SagaRetryOptions`** + **`ISagaStoreRegistrar`** — backend-agnostic
  retry knobs and a typed registrar indirection so durable backends
  swap themselves in without `MakeGenericType`.
- **EfCore-aware handler emit** — when the generator detects
  `WithEfCoreStore` in the same compilation, the emitted notification
  handlers wrap the load → step → save loop in a configurable retry
  catching `DbUpdateConcurrencyException`.
- **2 new diagnostics**:
  - `ZASAGA014` (Error) — saga state field has an unsupported type.
  - `ZASAGA015` (Info, suppressible) — saga commands should be idempotent
    under durable backends; fires when `WithEfCoreStore` / `WithRedisStore`
    is detected in the same compilation.
- **Implicit `AddMediator()`** — `AddSaga()` no longer requires a separate
  `services.AddMediator()` call.
- **InMemory backend unchanged** — v1.0 users see no behavioural change.

### `ZeroAlloc.Saga.EfCore` 1.0.0 (new package)

First durable backend for `ZeroAlloc.Saga`. Single shared `SagaInstance`
table keyed by `(SagaType, CorrelationKey)`; provider-agnostic row-version
optimistic concurrency; automatic retry-on-conflict driven by
`EfCoreSagaStoreOptions`. See
[`docs/persistence-efcore.md`](persistence-efcore.md) for the full
guide.

```csharp
services.AddDbContext<AppDbContext>(opts => opts.UseSqlServer(connStr));

services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .WithOrderFulfillmentSaga();
```

Plus, in your `DbContext`:

```csharp
protected override void OnModelCreating(ModelBuilder mb) => mb.AddSagas();
```
