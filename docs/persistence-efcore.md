# Durable persistence with `ZeroAlloc.Saga.EfCore`

`ZeroAlloc.Saga.EfCore` is a drop-in durable backend for `ZeroAlloc.Saga`.
A single shared `SagaInstance` table holds every saga instance keyed by
`(SagaType, CorrelationKey)`. Optimistic concurrency rides on a
`RowVersion` column; conflicts retry automatically inside the
generator-emitted notification handler.

## When to choose EfCore over InMemory

| Concern | InMemory (`ZeroAlloc.Saga` default) | EfCore (`ZeroAlloc.Saga.EfCore`) |
|---|---|---|
| Process-crash safety | None — all state lost | Row in DB; saga resumes after restart |
| Multi-instance hosting | Single-instance only | Multi-instance with row-version OCC |
| Correlation-key cardinality | Bounded by `SagaLockManager` (~80 B/key) | Bounded by table size |
| Setup cost | One line: `services.AddSaga()` | DbContext + migration + `WithEfCoreStore<TContext>()` |
| AOT-published binary | Fully supported | Library is AOT-clean; runtime needs JIT today (EF Core 9.0 limitation) |

If your sagas live entirely inside a single process and you can tolerate
losing in-flight workflows on restart, stay on InMemory. As soon as you
need to survive a crash or run more than one instance, switch to EfCore.

## Installation

```bash
dotnet add package ZeroAlloc.Saga.EfCore
```

The package depends on `Microsoft.EntityFrameworkCore.Relational` and
transitively on `ZeroAlloc.Saga`. Pick a provider package
(`Microsoft.EntityFrameworkCore.Sqlite` /
`Microsoft.EntityFrameworkCore.SqlServer` /
`Npgsql.EntityFrameworkCore.PostgreSQL`) yourself.

## Wiring

Two parts:

### 1. Add `SagaInstance` to your `DbContext`

```csharp
using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Saga.EfCore;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddSagas(); // configures SagaInstance entity
    }
}
```

`mb.AddSagas()` registers a single entity, `SagaInstanceEntity`, with:

- Composite primary key on `(SagaType, CorrelationKey)`
- `State` column — opaque `byte[]` payload produced by the generator-emitted
  serializer
- `CurrentFsmState` column — string name of the FSM state
- `RowVersion` column — `byte[]` concurrency token (rotated on every save)
- `(SagaType, UpdatedAt)` covering index for housekeeping queries

### 2. Wire the backend on the `ISagaBuilder`

```csharp
services.AddDbContext<AppDbContext>(opts => opts.UseSqlServer(connStr));

services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .WithOrderFulfillmentSaga(); // generator-emitted per-saga registration
```

> **Order matters.** `WithEfCoreStore<TContext>()` MUST be called
> BEFORE per-saga `WithXxxSaga()` registrations. Calling it after a
> saga is added throws `InvalidOperationException` with a reorder
> hint — the per-saga registration captures `SagaRetryOptions` at
> registration time, so rebinding it later would silently change
> retry behaviour for already-registered sagas.

## Migrations

`ZeroAlloc.Saga.EfCore` does not auto-migrate. After adding
`mb.AddSagas()` to your DbContext, generate an EF Core migration:

```bash
dotnet ef migrations add AddSagaInstance --project src/MyApp.Data
dotnet ef database update --project src/MyApp.Data
```

The migration adds the `SagaInstance` table + index. From there,
schema changes follow the same review cycle as the rest of your
DbContext.

## Optimistic concurrency (OCC)

The `RowVersion` column is mapped via `IsConcurrencyToken()` (NOT
`IsRowVersion()`), so the same code works across SQL Server's native
`ROWVERSION`, PostgreSQL's `xmin`, and SQLite (which has no native
row-version column). The store rotates `RowVersion` to a fresh
`Guid.NewGuid().ToByteArray()` on every save. EF includes the OLD
value in the `WHERE` clause, so a stale write affects zero rows and
surfaces as `DbUpdateConcurrencyException`.

The generator-emitted notification handler catches that exception and
retries the entire load → step → save loop. After
`MaxRetryAttempts` consecutive conflicts the handler gives up and
re-throws so the failure is visible to your message-bus consumer.

### Idempotency expectation

Because retries replay the saga step (and therefore re-dispatch
commands), step methods MUST be idempotent. The compiler emits
`ZASAGA015` (Info, suppressible) when it detects `WithEfCoreStore` in
the same compilation as a saga, reminding you to design commands
that tolerate at-least-once delivery. Typical patterns:

- Stamp commands with a deterministic id derived from the saga's
  correlation key + step number, then dedupe on the receiver side.
- Make handlers naturally idempotent (e.g. `INSERT OR IGNORE` on a
  unique key, conditional updates).

The optional `ZeroAlloc.Saga.Outbox` bridge wraps step-command dispatch
in the same transaction as the state save — the dispatch row commits
or rolls back atomically with the saga update. Combined with the
generator-emitted scope-per-attempt retry loop, this guarantees that
every step command is dispatched **exactly once** across both
cross-process races and same-process OCC retries. See
[`docs/outbox.md`](outbox.md). The idempotency guidance above remains
good practice for residual at-least-once cases (handler crashes
between save and message-bus ack, poller crashes after dispatch but
before `MarkSucceededAsync`).

## `EfCoreSagaStoreOptions`

Pass a configuration delegate to `WithEfCoreStore<TContext>()`:

```csharp
services.AddSaga()
    .WithEfCoreStore<AppDbContext>(opts =>
    {
        opts.MaxRetryAttempts = 5;                       // default 3
        opts.RetryBaseDelay = TimeSpan.FromMilliseconds(50); // default 10 ms
        opts.UseExponentialBackoff = true;               // default true
    })
    .WithOrderFulfillmentSaga();
```

| Option | Default | Effect |
|---|---|---|
| `MaxRetryAttempts` | 3 | Max OCC retries before re-throwing |
| `RetryBaseDelay` | 10 ms | Base wait between retries |
| `UseExponentialBackoff` | `true` | Delay grows as `base * 2^(attempt-1)` |

`EfCoreSagaStoreOptions` derives from `SagaRetryOptions` so the
generator-emitted handler can read the backend-agnostic surface
without knowing about EF Core.

## Provider notes

- **SQLite** — no native row-version. The store's manual GUID
  rotation provides equivalent OCC semantics. Useful for tests and
  single-machine deployments.
- **SQL Server** — works with the manual `byte[]` token. If you want
  SQL Server's native `ROWVERSION`, change `RowVersion` mapping in
  your own migration; the store accepts whatever bytes are present.
- **PostgreSQL** — same as SQL Server: the manual rotation is
  sufficient. PostgreSQL's `xmin` system column would also work but
  requires provider-specific configuration; not needed.

## Native AOT

The runtime library `ZeroAlloc.Saga.EfCore` builds clean under
`IsAotCompatible=true` + `EnableTrimAnalyzer` +
`EnableSingleFileAnalyzer` with zero warnings. That is the
load-bearing AOT contract for downstream JITted hosts (the runtime
ships AOT-safe annotations, doesn't pull in reflection-based
analyzer assets, etc.).

True end-to-end **`PublishAot=true`** for an EF Core 9.0 application
is still experimental upstream:

- The EF Core team marks `DbContext` ctor + `EnsureCreatedAsync`
  with `RequiresUnreferencedCode` / `RequiresDynamicCode`.
- Runtime model-building is disabled when `PublishAot=true`; you
  must pre-generate a compiled model with
  `dotnet ef dbcontext optimize --nativeaot`.
- LINQ queries inside the saga store currently require dynamic
  code; precompiled queries (`--precompile-queries`) cover only a
  subset of expressions and don't yet handle the store's tracked
  `Set<>().AsTracking().FirstOrDefaultAsync(...)` shape.

The `samples/AotSmokeEfCore/` smoke test enables the trim/AOT
analyzers (so the build catches regressions) but executes under JIT
to verify the saga round-trip end-to-end. When EF Core's AOT story
matures we'll flip the sample to `PublishAot=true` and the
`aot-smoke` workflow's EfCore job will publish + run a native
binary.

If you need a fully AOT-published saga today, stay on the InMemory
backend.

## Known limitations (v1.0 of `ZeroAlloc.Saga.EfCore`)

- **No schema-evolution migration.** v1.0 ships exactly one schema;
  changing `SagaInstanceEntity` requires a follow-up migration in
  your own EF migration history. Saga state shape changes that
  break the generator-emitted serializer manifest as
  `SagaStateVersionMismatchException` at load time.
- **Single-table per family.** Every saga in the same `DbContext`
  shares one `SagaInstance` table. Sharding by saga type (separate
  tables) is not supported in v1.0; lifts in a future minor.
- **Single `DbContext` per process.** `WithEfCoreStore<TContext>()`
  registers a `DbContext` alias scoped to `TContext`, so wiring
  two distinct contexts in the same `AddSaga()` builder is not
  supported. (BACKLOG #21 lifts this in a follow-up.)
- **Native AOT publish** — see the section above. Runtime library is
  AOT-clean; full publish blocked by upstream EF Core experimental
  status.

## See also

- [`samples/AotSmokeEfCore/`](../samples/AotSmokeEfCore/) — minimal saga
  end-to-end with EfCore + SQLite in-memory; AOT-clean build.
- [`samples/OrderFulfillment/`](../samples/OrderFulfillment/) —
  full demo. Pass `--efcore` to wire the EfCore backend.
- [`docs/concepts.md`](concepts.md) — saga lifecycle and generator output.
- [`docs/diagnostics.md`](diagnostics.md) — every `ZASAGA0XX`
  diagnostic with examples (including `ZASAGA015` for the
  idempotency reminder).
