# Migrating to v5

Every Saga package moves to 5.0.0 together. Three things change.

## .NET 10 or later

Every package now targets `net10.0` and `net11.0`. The `net8.0` assets are gone: .NET 8 went out
of support on 2026-11-10. An application on .NET 8 or .NET 9 stays on Saga 4.x until it moves to
.NET 10.

`ZeroAlloc.Saga.EfCore` keeps its floor on the EF Core 10 line on both frameworks, so a .NET 11
application can still use an EF Core 10 provider.

## `WithOrmStore()` with the ORM outbox store needs `WithOrmOutbox()`

`WithOrmStore().WithOutbox()` together with `AddOutbox().WithOrm()`, but without `WithOrmOutbox()`,
used to start and log a warning. It now fails the host start with an `InvalidOperationException`.
In that setup each outbox row was written when the step dispatched, before the saga state was
saved, so every concurrency retry enqueued the command again. Add `WithOrmOutbox()` from
`ZeroAlloc.Saga.Outbox.Orm` after `WithOutbox()`:

```csharp
services.AddOutbox(o => ...).WithOrm();
services.AddSaga()
    .WithOrmStore()
    .WithOutbox()
    .WithOrmOutbox()
    .WithOrderFulfillmentSaga();
```

The outbox rows then commit in the saga store's transaction. See
[the outbox bridge](outbox.md#zeroallocsagaorm-atomic-with-withormoutbox).

## `SagaLockManager<TKey>.AcquireAsync` returns `SagaLock`

`AcquireAsync` returns `ValueTask<SagaLock>` instead of `ValueTask<IDisposable>`. `SagaLock` is a
struct, and the manager reuses its per-key entries, so taking the lock of an idle key no longer
allocates.

- Code the Saga generator emits needs nothing but a rebuild. An assembly built against Saga 4.x
  that holds generated saga handlers fails with `MissingMethodException` until it is rebuilt.
- Code that calls `AcquireAsync` itself compiles unchanged with `using var _ = await ...`. Code
  that stores the result as `IDisposable` either stores a `SagaLock` or boxes it, which allocates.
- Dispose a `SagaLock` exactly once. A second `Dispose` throws `InvalidOperationException`. In 4.x
  it threw only when no other caller was waiting; after a hand-off it silently released the next
  holder's lock. It never releases a later holder now.
