# Atomic command dispatch with `ZeroAlloc.Saga.Outbox`

`ZeroAlloc.Saga.Outbox` is an opt-in bridge package that routes every saga
step's command through the [transactional outbox][outbox] persisted by
`ZeroAlloc.Outbox` (and any backend that implements `IOutboxStore`,
including `ZeroAlloc.Outbox.EfCore`). The dispatch row is committed in the
same database transaction as the saga state save, eliminating the
cross-process race where a saga state update can succeed without the
corresponding command being delivered (or vice versa).

[outbox]: https://microservices.io/patterns/data/transactional-outbox.html

> **Status:** ships in v1.2 alongside `ZeroAlloc.Saga` 1.2 and
> `ZeroAlloc.Saga.Outbox` 1.0. Requires `ZeroAlloc.Outbox` 2.4.0+
> (`EnqueueDeferredAsync` default-interface-method) and
> `ZeroAlloc.Serialisation` 2.1.0+.

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
   row in one round-trip.
3. A long-running `OutboxSagaCommandPoller` (registered by `WithOutbox`)
   reads pending entries, deserialises via the generator-emitted
   `ZeroAlloc.Saga.Generated.SagaCommandRegistry`, and dispatches each
   command through the consumer's `IMediator`.
4. After successful dispatch the entry is marked succeeded; on failure
   the entry is rescheduled (or dead-lettered after
   `OutboxSagaPollerOptions.MaxRetries`).

When a losing scope's `SaveChangesAsync` raises
`DbUpdateConcurrencyException` and that scope is then disposed (the
typical cross-process and cross-scope shape — different replicas, or
explicit `IServiceScope` per attempt), the tracked outbox row is
discarded along with the failed saga update. A retry from a fresh
scope cannot result in a phantom dispatch. That's the durable
atomicity guarantee this package adds for the cross-process race.

In-process OCC retries that **reuse the same scope** behave
differently — see "Same-process OCC retry still has at-least-once
semantics" under Limitations below.

## When to use it

| Scenario | Recommendation |
|---|---|
| `ZeroAlloc.Saga.EfCore` backend | **Use the bridge.** This is the primary deployment shape it was designed for. |
| `ZeroAlloc.Saga` InMemory backend | Don't bother. InMemory writes are atomic by construction; the bridge adds latency and a poller for no benefit. |
| Cross-process / multi-replica deployments | **Use the bridge.** This is exactly the race it fixes. |
| Single-process, single-replica, fire-and-forget commands | Optional; the bridge converts synchronous dispatch into asynchronous dispatch (poller cadence). Either is correct. |

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
//    AddOutbox().WithEfCore<TContext>() registers IOutboxStore as Scoped so
//    EfCoreOutboxStore<T> resolves the same scoped DbContext as the saga store
//    — that shared scope is what makes the dispatch row commit atomically with
//    the saga state save.
services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(connectionString),
    ServiceLifetime.Scoped);

services.AddOutbox().WithEfCore<AppDbContext>();

services.AddMediator();
services.AddSaga()
    .WithEfCoreStore<AppDbContext>(opts => opts.MaxRetryAttempts = 3)
    .WithOutbox()                        // <-- replaces dispatcher + adds poller
    .AddOrderFulfillmentSaga();

// 3. Per-command serialiser registration.
//    The bridge resolves ISerializer<TCommand> from DI for each step command.
//    Use ZeroAlloc.Serialisation.SystemTextJson for the JSON adapter, or roll
//    a hand-tuned ISerializer<T> for hot paths.
services.AddSingleton<ISerializer<ReserveStockCommand>, JsonCommandSerializer<ReserveStockCommand>>();
services.AddSingleton<ISerializer<ChargeCustomerCommand>, JsonCommandSerializer<ChargeCustomerCommand>>();
// ...one per step command.
```

`WithOutbox()` does three things:

- Replaces the default scoped `ISagaCommandDispatcher` with
  `OutboxSagaCommandDispatcher`.
- Lazily registers a `SagaCommandRegistryDispatcher` singleton that locates
  the generator-emitted `ZeroAlloc.Saga.Generated.SagaCommandRegistry` via
  reflection on first poller cycle.
- Adds `OutboxSagaCommandPoller` as a hosted service.

## Marking step command types `partial`

The poller deserialises commands through the generator-emitted
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

## Poller knobs

```csharp
services.AddSingleton(new OutboxSagaPollerOptions
{
    PollInterval = TimeSpan.FromSeconds(2),
    BatchSize    = 32,
    MaxRetries   = 5,
    RetryDelay   = TimeSpan.FromSeconds(10),
});
```

| Option | Default | Effect |
|---|---|---|
| `PollInterval` | 2 s | Sleep between cycles |
| `BatchSize` | 32 | Max entries fetched per cycle |
| `MaxRetries` | 5 | Total dispatch attempts before dead-letter |
| `RetryDelay` | 10 s | Delay added to `UtcNow` when scheduling the next retry |

Per-entry failure isolation: a single dispatch failure does not poison the
batch; the entry is rescheduled (or dead-lettered) and the poller continues
with the next entry.

## Limitations

### Same-process OCC retry still has at-least-once semantics

The generator-emitted handler's retry loop reuses the same scoped
`DbContext` across attempts. EF Core does not clear the `ChangeTracker`
when `SaveChangesAsync` throws `DbUpdateConcurrencyException`, so a retried
`EnqueueDeferredAsync` adds a *second* tracked outbox row alongside the
first. If a later attempt succeeds, both rows commit and the poller
dispatches the command twice.

The bridge therefore tightens **persistence atomicity** (the dispatch row
and the saga update commit together or roll back together when scopes are
disposed) but does **not** eliminate the at-least-once-from-mediator's-view
quirk that `ZASAGA015` already documents for in-process OCC retries. Step
command handlers must remain idempotent.

A scope-per-attempt retry loop is a candidate follow-up that would close
this gap.

### Shared scoped `DbContext` is required

The atomicity guarantee depends on `EfCoreSagaStore` and
`EfCoreOutboxStore` resolving the **same** scoped `DbContext`. Both use
constructor injection of `TDbContext`, so the standard
`AddDbContext<TDbContext>(..., ServiceLifetime.Scoped)` registration
satisfies this naturally. Don't register the saga store and the outbox
store against different `DbContext` types in the same scope.

### Cross-assembly step command types

`ZASAGA017` fires when a step command type is declared in a separate
assembly. The auto-`[ZeroAllocSerializable]` partial-extension generator
can't reach across assemblies; the consumer must apply the attribute
themselves on the source-of-truth declaration.

### Default-interface-method fallback

`IOutboxStore.EnqueueDeferredAsync` is a default-interface-method that
falls back to `EnqueueAsync(transaction: null, ct)` when not overridden.
A backend that does not override it auto-commits each enqueue, defeating
the atomicity premise. Use `ZeroAlloc.Outbox.EfCore` 2.4.0+ (which
overrides) — or any third-party backend that explicitly overrides
`EnqueueDeferredAsync` to defer the write to the caller's
`SaveChangesAsync` (or equivalent).

## See also

- [`docs/persistence-efcore.md`](persistence-efcore.md) — base
  `Saga.EfCore` setup, OCC retry, idempotency expectation
  (`ZASAGA015`).
- [`docs/diagnostics.md`](diagnostics.md) — full diagnostic catalog
  including `ZASAGA015` / `ZASAGA016` / `ZASAGA017`.
- `ZeroAlloc.Outbox` documentation — backend-side outbox semantics,
  poller patterns, dead-letter queue management.
