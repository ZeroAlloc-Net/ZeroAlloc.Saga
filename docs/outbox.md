# Atomic command dispatch with `ZeroAlloc.Saga.Outbox`

`ZeroAlloc.Saga.Outbox` is an opt-in bridge package that routes every saga
step's command through the [transactional outbox][outbox] persisted by
`ZeroAlloc.Outbox` (and any backend that implements `IOutboxStore`,
including `ZeroAlloc.Outbox.EfCore`). With the EF Core saga store, the
dispatch row is committed in the same database transaction as the saga
state save, eliminating the cross-process race where a saga state update
can succeed without the corresponding command being delivered (or vice
versa). The Redis saga store gets the same guarantee from
`WithRedisOutbox()`, see [`outbox-redis.md`](outbox-redis.md).

> **Not atomic on `ZeroAlloc.Saga.Orm`.** With `WithOrmStore()`, the outbox row is written as
> soon as the step dispatches, before the saga state is saved. Dispatch is at-least-once, and
> step command handlers must be idempotent. See
> [ZeroAlloc.Saga.Orm: at-least-once](#zeroallocsagaorm-at-least-once).

[outbox]: https://microservices.io/patterns/data/transactional-outbox.html

> **Status:** `ZeroAlloc.Saga.Outbox` 4.0 requires `ZeroAlloc.Outbox` 3.0.1 or later and
> `ZeroAlloc.Serialisation` 2.1.0 or later. Not 3.0.0: its EfCore package throws
> `TypeLoadException` on .NET 10 with EF Core 10, fixed in
> [ZeroAlloc.Outbox#208](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/208). EF Core
> users also need `ZeroAlloc.Outbox.EfCore` 3.0.1 or later; Saga's floor on `ZeroAlloc.Outbox`
> does not raise it. Upgrading from 3.x? See [Migrating to v4](migrating-to-v4.md).

## What it fixes

Without the outbox bridge, a generator-emitted saga handler dispatches the
step command via `IMediator.Send` *before* `ISagaStore.SaveAsync`. If the
state save fails (OCC conflict, network blip), the command has already
been dispatched and there is no transactional path to undo it. The
documented mitigation in v1.1 is `ZASAGA015` ("step commands should be
idempotent") — at-least-once-from-mediator's-view delivery, with the
deduplication burden pushed to the receiver.

With the outbox bridge:

1. `OutboxSagaCommandDispatcher.DispatchAsync<T>(cmd, ct)` resolves
   `ISerializer<T>` from DI, serialises the command, and calls
   `IOutboxStore.EnqueueDeferredAsync(typeName, payload, ct)`. With the
   `EfCore` backend, this `Add`s a tracked `OutboxMessageEntity` to the
   shared scoped `DbContext` but does **not** call `SaveChangesAsync`.
2. The saga store's `SaveAsync` calls `SaveChangesAsync` on the same
   scoped `DbContext`, committing both the saga update and the outbox
   row in one round-trip. On the step that completes the saga, and when
   compensation finishes, the handler calls `RemoveAsync` instead, which
   commits the outbox rows with the row deletion the same way. It commits
   them even when no saga row exists, as for a saga that one event both
   starts and completes.
3. ZeroAlloc.Outbox's `OutboxWorkerService`, registered by `AddOutbox()`, claims pending
   entries under a lease. For each saga command type, `WithOutbox()` registered an
   `IOutboxTypeDispatcher` that deserialises the command through the generator-emitted
   `ZeroAlloc.Saga.Generated.SagaCommandRegistry` of the assembly that declares the saga, and
   sends it through that assembly's `IMediator`.
4. After a successful dispatch the worker marks the entry succeeded. On failure it reschedules
   the entry with exponential backoff, or dead-letters it after `OutboxOptions.MaxAttempts`
   attempts.

When `SaveChangesAsync` raises `DbUpdateConcurrencyException` (or
`DbUpdateException` for fresh-key INSERT races), the failing attempt's
`IServiceScope` is disposed by the generated handler — its tracked
outbox row goes away with the rolled-back saga update. The handler
then retries in a fresh scope with a fresh `DbContext`. A retry that
eventually succeeds commits exactly one outbox row, and the worker
dispatches the command exactly once. Same guarantee for cross-process
races (each replica has its own scope) and same-process OCC retries
(scope-per-attempt makes them equivalent).

## When to use it

| Scenario | Recommendation |
|---|---|
| `ZeroAlloc.Saga.EfCore` backend | **Use the bridge.** This is the primary deployment shape it was designed for. |
| `ZeroAlloc.Saga.Redis` backend | **Use the bridge with `WithRedisOutbox()`**, which makes it atomic. Without it, dispatch is at-least-once. See [`outbox-redis.md`](outbox-redis.md). |
| `ZeroAlloc.Saga.Orm` backend | **At-least-once, not atomic.** The bridge still makes dispatch durable and asynchronous, but an OCC retry can enqueue a command twice. Step command handlers must be idempotent. See [below](#zeroallocsagaorm-at-least-once). |
| `ZeroAlloc.Saga` InMemory backend | Don't bother. InMemory writes are atomic by construction; the bridge adds latency and a worker for no benefit. |
| Cross-process / multi-replica deployments | **Use the bridge** with EF Core, or with Redis and `WithRedisOutbox()`. This is exactly the race it fixes. |
| Single-process, single-replica, fire-and-forget commands | Optional; the bridge converts synchronous dispatch into asynchronous dispatch (worker cadence). Either is correct. |

## Wiring

```csharp
// 1. DbContext that materialises BOTH schemas — saga state + outbox messages.
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddSagas();           // SagaInstance schema (Saga.EfCore)
        modelBuilder.AddOutboxMessages();  // OutboxMessage schema (Outbox.EfCore)
    }
}

// 2. Service registration. AddDbContext registers the DbContext as Scoped;
//    WithEfCore<TContext>() registers IOutboxStore as Scoped so EfCoreOutboxStore<T>
//    resolves the same scoped DbContext as the saga store — that shared scope is what
//    makes the dispatch row commit atomically with the saga state save.
services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connectionString),
    ServiceLifetime.Scoped);

// ZeroAlloc.Outbox: the worker that dispatches saga commands, and its EF store.
// Call AddOutbox() exactly once.
services.AddOutbox(o =>
{
    o.PollingInterval = TimeSpan.FromSeconds(2);
    o.LeaseDuration   = TimeSpan.FromMinutes(2); // longer than your slowest command handler
}).WithEfCore<AppDbContext>();

services.AddMediator();
services.AddSaga()
    .WithEfCoreStore<AppDbContext>(opts => opts.MaxRetryAttempts = 3)
    .WithOutbox()                        // <-- replaces the dispatcher, registers saga dispatchers
    .WithOrderFulfillmentSaga();

// 3. Per-command serialiser registration.
//    The bridge resolves ISerializer<TCommand> from DI for each step command.
//    Use ZeroAlloc.Serialisation.SystemTextJson for the JSON adapter, or roll
//    a hand-tuned ISerializer<T> for hot paths.
services.AddSingleton<ISerializer<ReserveStockCommand>, JsonCommandSerializer<ReserveStockCommand>>();
services.AddSingleton<ISerializer<ChargeCustomerCommand>, JsonCommandSerializer<ChargeCustomerCommand>>();
// ...one per step command.
```

`AddOutbox()` and `AddSaga()` can come in either order. `WithOutbox()` does not call
`AddOutbox()`: a second `AddOutbox()` would start a second worker
([ZeroAlloc.Outbox#206](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/206)).

`WithOutbox()` does four things:

- Replaces the default scoped `ISagaCommandDispatcher` with
  `OutboxSagaCommandDispatcher`.
- Registers one scoped `IOutboxTypeDispatcher` for each saga step and compensation command
  type. The types come from the `SagaCommandSource` that every generator-emitted
  `With{Saga}()` registers, one per assembly that declares sagas, so sagas may be split across
  projects. `With{Saga}()` calls may come before or after `WithOutbox()`. Nothing is found by
  reflection. The worker resolves the dispatchers from the same per-batch scope as the store, so
  dispatch shares the store's `DbContext`.
- Registers the `SagaCommandRegistryDispatcher` delegate those dispatchers call. The default
  routes each command to the source of the assembly that declares it. Register your own before
  `WithOutbox()` to replace it, for example in tests.
- Registers a startup check, described below.

See [Sagas in more than one assembly](concepts.md#sagas-in-more-than-one-assembly).

### Startup check

`WithOutbox()` also registers an `IHostedLifecycleService`. Its check runs in `StartingAsync`,
which the host calls on every such service before it starts any `IHostedService`, so
`OutboxWorkerService` cannot claim a saga row before the check has passed. It throws
`InvalidOperationException` with one of these messages:

- No saga is registered on the service collection:

  > ZeroAlloc.Saga.Outbox.WithOutbox(): no saga is registered, so there is no saga command to
  > dispatch. Register your sagas on the same service collection with their generator-emitted
  > With{Saga}(), before or after WithOutbox(). A saga assembly built with a Saga generator older
  > than this ZeroAlloc.Saga.Outbox registers no saga command source; rebuild it.

- An assembly's sagas are registered, but the assembly does not reference
  `ZeroAlloc.Serialisation`, so the generator emitted no `SagaCommandRegistry` to deserialize its
  commands. The message names each such assembly:

  > ZeroAlloc.Saga.Outbox.WithOutbox(): the saga commands of 'Billing' cannot be dispatched from
  > the outbox. [...] Add a ZeroAlloc.Serialisation package reference to the project that
  > declares those [Saga] classes.

- No `OutboxWorkerService` is registered, meaning `AddOutbox()` was never called:

  > ZeroAlloc.Saga.Outbox.WithOutbox(): no OutboxWorkerService is registered. ZeroAlloc.Outbox's
  > worker dispatches saga commands, and WithOutbox() does not register it: call
  > services.AddOutbox() once, before or after AddSaga().

- No `IOutboxStore` resolves, meaning no store was wired up:

  > ZeroAlloc.Saga.Outbox.WithOutbox(): no IOutboxStore is registered. Register one with
  > AddOutbox().WithEfCore\<TContext>(), or with WithRedisOutbox() when the saga store is Redis.

- Two `IOutboxTypeDispatcher`s claim the same saga command type name — see below.

The missing-worker and missing-store messages also print the supported EF and Redis setups shown under Wiring above.

### One dispatcher per type name

The worker keeps a single `IOutboxTypeDispatcher` per type name: when two claim the same name,
the last registration wins and the other never runs. A saga command's type name is its
`Type.FullName`. If an `[OutboxMessage]` type or a hand-written dispatcher uses the same name, the
startup check throws:

> ZeroAlloc.Saga.Outbox.WithOutbox(): another IOutboxTypeDispatcher is registered for the saga
> command type '\<TypeName>'. ZeroAlloc.Outbox's worker keeps one dispatcher per type name, so one
> of the two would never run. Remove the other registration, or give its message type a different
> name.

Rename one of the types or remove the other registration.

### Telemetry

`ZeroAlloc.Outbox.Telemetry`'s `WithTelemetry()` decorates the dispatchers registered before it
runs. To instrument saga dispatch too, call it after `WithOutbox()`:

```csharp
var outbox = services.AddOutbox().WithEfCore<AppDbContext>();
services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .WithOutbox()
    .WithOrderFulfillmentSaga();
outbox.WithTelemetry();
```

## Dispatch options

Outbox's worker dispatches saga commands, so you configure dispatch through `AddOutbox(o => …)`:

| Option | Default | Effect |
|---|---|---|
| `PollingInterval` | 5 s | Delay between polling cycles |
| `BatchSize` | 50 | Max entries claimed per cycle |
| `MaxAttempts` | 5 | Total dispatch attempts before dead-letter |
| `RetryBaseDelay` | 2 s | First retry delay; each further failure doubles it |
| `LeaseDuration` | 5 min | How long a claim lasts. Must exceed the slowest single dispatch |
| `HostId` | machine name + a random value | Identifies this process as a lease holder. Must be unique per process |

The 3.x `OutboxSagaPollerOptions` map onto them like this:

| 3.x | 4.0 | Note |
|---|---|---|
| `PollInterval` (default 2 s) | `PollingInterval` | Set it to 2 s to keep the old cadence |
| `BatchSize` (default 32) | `BatchSize` | |
| `MaxRetries` (default 5) | `MaxAttempts` | Same meaning: total attempts |
| `RetryDelay` (default 10 s, fixed) | `RetryBaseDelay` | Now exponential: base, 2 × base, 4 × base, … |

Per-entry failure isolation: a single dispatch failure does not poison the
batch; the entry is rescheduled (or dead-lettered) and the worker continues
with the next entry.

## Leases and multiple hosts

ZeroAlloc.Outbox 3.0 claims entries under a lease. Each host claims a batch atomically, renews
an entry's lease right before dispatching it, records the outcome only while it still holds the
lease, and releases what it did not finish when it stops. Two hosts polling the same store never
dispatch the same saga command, unless a single dispatch outlives `LeaseDuration`. See
ZeroAlloc.Outbox's [claim, renew and release model](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/outbox-pattern.md#the-claim-renew-and-release-model).

Hosts need roughly synchronised clocks, which NTP gives you: each host compares lease expiry
times written from its own clock. Delivery stays at-least-once. A duplicate now needs a dispatch
that outlives `LeaseDuration`, or a host that dies after dispatching but before marking.

## Native AOT

`AddOutbox()` is `[RequiresUnreferencedCode]`, because it may register a reflection-based JSON
serializer that saga dispatch never uses. Until ZeroAlloc.Outbox offers an AOT-clean registration
([ZeroAlloc.Outbox#207](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/207)),
a `PublishAot` app registers what the worker needs itself:

```csharp
services.AddOptions<OutboxOptions>().Configure(o => o.PollingInterval = TimeSpan.FromSeconds(2));
services.AddHostedService<OutboxWorkerService>();
```

This skips the `OutboxOptions` validation that `AddOutbox()` adds, so check the values yourself.
Register the `IOutboxStore` yourself as well, or use `WithRedisOutbox()`, which supplies it.
`WithEfCore<T>()` hangs off the `IOutboxBuilder` that only `AddOutbox()` returns.
`samples/AotSmokeOutbox` runs this setup under ILC in CI.

## Marking step command types `partial`

Outbox dispatch deserialises commands through the generator-emitted
`SagaCommandRegistry`, which routes by `typeof(T).FullName!` and resolves
the per-command `ISerializer<T>` from DI. For the generator to auto-apply
`[ZeroAllocSerializable(SerializationFormat.SystemTextJson)]` (so
`ZeroAlloc.Serialisation`'s analyzer is satisfied), every step command type
must be declared `partial` in the consumer compilation:

```csharp
public readonly partial record struct ReserveStockCommand(OrderId OrderId, decimal Total)
    : IRequest<Unit>;
```

The saga generator emits two diagnostics to nudge users to the right shape:

- **`ZASAGA016`** (Warning, suppressible) — fires when a step command type
  is not `partial`. The auto-attribute extension cannot be emitted, so the
  command will fail `ZeroAlloc.Serialisation`'s analyzer at the consumer's
  build. A code-fix is provided that adds the `partial` modifier.
- **`ZASAGA017`** (Info) — fires when a step command type is declared in a
  different assembly than the saga. The generator can't emit a partial
  extension across assembly boundaries; the consumer must apply
  `[ZeroAllocSerializable]` themselves on the source-of-truth type.

## Single-dispatch under OCC retry

This section describes the EF Core store. The Redis store with `WithRedisOutbox()` gives the
same guarantee. The ORM store does not, see
[ZeroAlloc.Saga.Orm: at-least-once](#zeroallocsagaorm-at-least-once).

The generator-emitted handler's retry loop creates a fresh
`IServiceScope` per attempt, so `ISagaStore<TSaga,TKey>` and
`ISagaCommandDispatcher` resolve a fresh `DbContext` on every iteration.
On `DbUpdateConcurrencyException`, the failed attempt's scope is disposed
— its tracked outbox row is discarded along with the rolled-back saga
update. A later attempt that succeeds commits exactly **one** outbox row,
and the worker dispatches the command exactly **once**.

This holds for both same-process retries (one consumer, one OCC
clash) and cross-process races (multiple replicas, one wins, the others
retry into a now-stale FSM trigger and silently no-op). `ZASAGA015`'s
"step commands must be idempotent" guidance remains good practice for
the rare residual cases (the saga handler itself crashes between
`SaveChangesAsync` and the message-bus ack, a worker dies after
dispatching but before `MarkSucceededAsync`, a dispatch outlives
`LeaseDuration`, etc.) but is no longer needed to defend against the
bridge's own retry path.

## Limitations

### Shared scoped `DbContext` is required

The atomicity guarantee depends on `EfCoreSagaStore` and
`EfCoreOutboxStore` resolving the **same** scoped `DbContext`. Both use
constructor injection of `TDbContext`, so the standard
`AddDbContext<TDbContext>(..., ServiceLifetime.Scoped)` registration
satisfies this naturally. Don't register the saga store and the outbox
store against different `DbContext` types in the same scope.

### ZeroAlloc.Saga.Orm: at-least-once

`WithOrmStore().WithOutbox()` works with ZeroAlloc.Outbox's ORM store, `AddOutbox().WithOrm()`,
but it is not atomic. Don't pair the ORM saga store with `WithEfCore<TContext>()`: the EF Core
outbox store defers its row to a `SaveChangesAsync` that the ORM saga store never calls, so the
command is lost. A startup check for that pairing is tracked in
[ZeroAlloc.Saga#199](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/199).

Why the ORM pairing is not atomic:

- `WithOutbox()` enlists each step command through `IOutboxStore.EnqueueDeferredAsync`.
  ZeroAlloc.Outbox's `OrmOutboxStore` does not override it, so the default writes the outbox
  row immediately, in its own statement.
- The saga state is saved afterwards, in a separate statement. The ORM store has no
  transaction that both writes share.

So when the save or removal raises `OrmSagaConcurrencyException`, the command's outbox row is
already committed. The generated handler retries the step in a fresh scope, and the retry
enqueues the command again. The worker then dispatches it twice. The same happens for any
other failure between the enqueue and the save.

Step command handlers must therefore be idempotent, as `ZASAGA015` recommends. A unit of work
that commits the outbox rows in the saga store's transaction is tracked in
[ZeroAlloc.Saga#197](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues/197).

### Cross-assembly step command types

`ZASAGA017` fires when a step command type is declared in a separate
assembly. The auto-`[ZeroAllocSerializable]` partial-extension generator
can't reach across assemblies; the consumer must apply the attribute
themselves on the source-of-truth declaration.

### Sagas declared across more than one assembly

Supported. Every assembly that declares sagas must reference `ZeroAlloc.Serialisation`, and each
saga command type must belong to the sagas of one assembly. See
[Sagas in more than one assembly](concepts.md#sagas-in-more-than-one-assembly).

### Default-interface-method fallback

`IOutboxStore.EnqueueDeferredAsync` is a default-interface-method that
falls back to `EnqueueAsync(transaction: null, ct)` when not overridden.
A backend that does not override it auto-commits each enqueue, defeating
the atomicity premise. `ZeroAlloc.Outbox.Orm` is such a backend, see
[above](#zeroallocsagaorm-at-least-once). Use `ZeroAlloc.Outbox.EfCore` (which
overrides) — or any third-party backend that explicitly overrides
`EnqueueDeferredAsync` to defer the write to the caller's
`SaveChangesAsync` (or equivalent).

## See also

- [`docs/migrating-to-v4.md`](migrating-to-v4.md) — upgrading from Saga 3.x.
- [`docs/persistence-efcore.md`](persistence-efcore.md) — base
  `Saga.EfCore` setup, OCC retry, idempotency expectation
  (`ZASAGA015`).
- [`docs/diagnostics.md`](diagnostics.md) — full diagnostic catalog
  including `ZASAGA015` / `ZASAGA016` / `ZASAGA017`.
- `ZeroAlloc.Outbox` documentation — backend-side outbox semantics,
  the worker, dead-letter queue management.
