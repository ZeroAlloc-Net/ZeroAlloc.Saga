# Migrating to v4

ZeroAlloc.Saga 4.0 moves the outbox bridge onto ZeroAlloc.Outbox 3.0's lease-based claim. Every
Saga package moves to 4.0.0 together. Only `ZeroAlloc.Saga.Outbox` and
`ZeroAlloc.Saga.Outbox.Redis` change in ways you have to act on. If you don't use the outbox
bridge, upgrading needs nothing but the version bump.

The floor is `ZeroAlloc.Outbox` **3.0.1**, not 3.0.0: 3.0.0's EfCore package throws
`TypeLoadException` on .NET 10 with EF Core 10, fixed in
[ZeroAlloc.Outbox#208](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/208). EF Core
users also need `ZeroAlloc.Outbox.EfCore` 3.0.1 or later; Saga's floor on `ZeroAlloc.Outbox`
does not raise it.

Later 4.x releases raise the floor to `ZeroAlloc.Outbox` **4.2.0**: 4.1.0 added the transactional
enqueue `ZeroAlloc.Saga.Outbox.Orm` needs, and 4.2.0 fixed the outbox's migration source name. Upgrade the Outbox adapter
package you use, such as `ZeroAlloc.Outbox.EfCore`, to 4.x with it; see
[`outbox.md`](outbox.md) for what ZeroAlloc.Outbox 4.0 changes.

## What got fixed

- **The documented EF setup dead-lettered saga commands.** `AddOutbox()` registers
  ZeroAlloc.Outbox's worker, and `WithOutbox()` registered a second poller of Saga's own. Both
  polled one table. The worker had no dispatcher for saga command types, so every saga row it
  reached first was dead-lettered with "No dispatcher for type …". Now only the worker polls, and
  it has a dispatcher for every saga command type.
- **The Redis outbox dispatched each command on every replica.** `RedisOutboxStore` read pending
  entries without claiming them, so every replica dispatched the same commands, and a late mark
  could put a finished command back into pending. It now claims under a lease, atomically.
- **Nested command types were never dispatched from the outbox.** The registry matched the
  Roslyn display name, `Outer.Command`, against the `Type.FullName` the bridge writes,
  `Outer+Command`. It now matches `Type.FullName`.

## `Add{Saga}Saga()` is removed

The legacy, `[Obsolete]`-marked `Add{Saga}Saga()` alias (diagnostic `ZASAGA018`) is gone. Rename
every call to the `With{Saga}Saga()` name it pointed at:

```csharp
// 3.x
services.AddSaga().AddOrderFulfillmentSaga();

// 4.0
services.AddSaga().WithOrderFulfillmentSaga();
```

This applies whether or not you use the outbox bridge.

## 1. Call `AddOutbox()` yourself

`WithOutbox()` no longer brings a poller. ZeroAlloc.Outbox's `OutboxWorkerService` dispatches
saga commands, and you register it with `AddOutbox()`, exactly once, before or after `AddSaga()`.

```csharp
// 3.x
services.AddOutbox().WithEfCore<AppDbContext>();   // often missing; the saga poller ran anyway
services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .WithOutbox()
    .WithOrderFulfillmentSaga();

// 4.0 — EF Core
services.AddOutbox(o => o.PollingInterval = TimeSpan.FromSeconds(2)).WithEfCore<AppDbContext>();
services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .WithOutbox()
    .WithOrderFulfillmentSaga();

// 4.0 — Redis: WithRedisOutbox() still supplies the IOutboxStore
services.AddOutbox(o => o.PollingInterval = TimeSpan.FromSeconds(2));
services.AddSaga()
    .WithRedisStore()
    .WithOutbox()
    .WithRedisOutbox()
    .WithOrderFulfillmentSaga();
```

When the host starts, a check fails the start if no `OutboxWorkerService` or no `IOutboxStore` is
registered, or if two `IOutboxTypeDispatcher`s claim the same saga command type name. It runs
before any hosted service starts, so nothing is dispatched from a broken setup. See
[`docs/outbox.md`](outbox.md#startup-check) for the exact messages it throws.

## 2. Removed types

| Removed | Instead |
|---|---|
| `OutboxSagaCommandPoller` | ZeroAlloc.Outbox's `OutboxWorkerService`, registered by `AddOutbox()` |
| `OutboxSagaPollerOptions` | `OutboxOptions`, set through `AddOutbox(o => …)` |

Tests that drove `OutboxSagaCommandPoller.PollOnceAsync` start the host instead and wait for the
effect. See `tests/ZeroAlloc.Saga.Outbox.Tests/E2ETests.cs`:

```csharp
using var host = new HostBuilder().ConfigureServices(services => { /* the setup above */ }).Build();
await host.StartAsync();
// wait until the command handler has run, then:
await host.StopAsync();
```

A `SagaCommandRegistryDispatcher` registered before `WithOutbox()` still replaces the default
dispatch through the generated command sources.

## 3. Options

| 3.x `OutboxSagaPollerOptions` | 4.0 `OutboxOptions` | Note |
|---|---|---|
| `PollInterval`, default 2 s | `PollingInterval`, default 5 s | Set 2 s to keep the old cadence |
| `BatchSize`, default 32 | `BatchSize`, default 50 | |
| `MaxRetries`, default 5 | `MaxAttempts`, default 5 | Same meaning: total attempts before dead-letter |
| `RetryDelay`, default 10 s, fixed | `RetryBaseDelay`, default 2 s | **Now exponential:** base, 2 × base, 4 × base, … |
| — | `LeaseDuration`, default 5 min | Must exceed your slowest command handler |
| — | `HostId`, default machine name + random value | Must be unique per process. Set it when several hosts share a process |

Two smaller differences:
- A dead-lettered entry now records the exception's message, where 3.x stored `ToString()`.
- Saga dispatch now emits ZeroAlloc.Outbox's `outbox.*` metrics and dashboard events.

## 4. EF Core: add the lease columns

ZeroAlloc.Outbox.EfCore 3.0's `OutboxMessageEntity` gained `LockedBy` and `LockedUntil`. Add a
migration before any 4.0 host starts:

```bash
dotnet ef migrations add AddOutboxLease --project src/YourProject.EfCore
dotnet ef database update
```

The details are in ZeroAlloc.Outbox's
[migration guide](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/migrating-to-v3.md#ef-core-users),
including the SQL Server compatibility level the claim needs. If your model converts
`DateTimeOffset` columns for SQLite, convert `LockedUntil` too:

```csharp
entity.Property(m => m.LockedUntil).HasConversion(nullableDtoConverter);
```

## 5. Redis

**Your data needs no migration.**
- Entries written by 3.x have no `lockedBy` or `lockedUntil`. That means unleased, so the
  first 4.0 claim picks them up as they are.
- 3.x marked an entry awaiting retry with `status=Failed`, and left it in the pending set. The
  4.0 claim treats `Failed` as pending and rewrites it on claim, so in-flight retries survive the
  upgrade.

Code that calls `RedisOutboxStore` directly follows the Outbox 3.0 contract:

| 3.x | 4.0 |
|---|---|
| `FetchPendingAsync(batchSize, ct)` | `ClaimPendingAsync(batchSize, lease, ct)` |
| — | `RenewLeaseAsync(id, lease, ct)` returns `bool` |
| — | `ReleaseLeasesAsync(ids, lease, ct)` returns `int` |
| `MarkSucceededAsync(id, ct)` | `MarkSucceededAsync(id, lease, ct)` returns `bool` |
| `MarkFailedAsync(id, retryCount, nextRetryAt, ct)` | `MarkFailedAsync(id, retryCount, nextRetryAt, lease, ct)` returns `bool` |
| `DeadLetterAsync(id, error, ct)` | `DeadLetterAsync(id, error, lease, ct)` returns `bool` |

A mark only applies to an entry the same lease claimed, so claim before you mark.

`ZeroAlloc.Saga.Outbox.Redis` does not support Redis Cluster, as in 3.x; see
[Redis Cluster](outbox-redis.md#redis-cluster).

## 6. Custom `IOutboxStore` implementations

A store you wrote yourself must implement the 3.0 interface. See ZeroAlloc.Outbox's
[guide for custom stores](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/blob/main/docs/migrating-to-v3.md#custom-ioutboxstore-implementers).

## 7. One dispatcher per type name

ZeroAlloc.Outbox's worker keeps one `IOutboxTypeDispatcher` per type name. A saga command's type
name is its `Type.FullName`. If another dispatcher, such as an `[OutboxMessage]` type's, uses the
same name, the host fails to start and names the type.

## 8. Native AOT

Under ZeroAlloc.Outbox 3.x, `AddOutbox()` was `[RequiresUnreferencedCode]`, and a `PublishAot`
app registered the worker directly. ZeroAlloc.Outbox 4.0 made `AddOutbox()` AOT-safe
([ZeroAlloc.Outbox#207](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox/issues/207)), and Saga
now requires 4.2.0, so call `AddOutbox()` in every app.
See [Native AOT in the outbox guide](outbox.md#native-aot).

## Rolling out

Apply the EF migration first; 3.x hosts ignore the new nullable columns and keep working. Then
stop every 3.x host before you start the first 4.0 host. Don't run them side by side: a 3.x host
reads due entries without claiming them and marks them unconditionally, so it can dispatch an
entry a 4.0 host is claiming at the same moment, and its unconditional mark can put an entry a
4.0 host already finished back into pending. Entries the 3.x hosts wrote, including ones awaiting
retry, are picked up by the first 4.0 claim.
