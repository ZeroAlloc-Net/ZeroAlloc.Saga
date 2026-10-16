# Packages and installation

```bash
dotnet add package ZeroAlloc.Saga          # runtime + generator (single package)
```

Optional packages:

| Package | Adds |
|---|---|
| `ZeroAlloc.Saga.EfCore` | Durable saga store over an EF Core `DbContext` (`WithEfCoreStore<TContext>()`) |
| `ZeroAlloc.Saga.Orm` | Durable saga store over ZeroAlloc.ORM (`WithOrmStore()`) |
| `ZeroAlloc.Saga.Redis` | Durable saga store over Redis (`WithRedisStore()`) |
| `ZeroAlloc.Saga.Outbox` | Step commands dispatched through ZeroAlloc.Outbox (`WithOutbox()`) |
| `ZeroAlloc.Saga.Outbox.Orm` | Outbox rows committed in the ORM saga store's transaction (`WithOrmOutbox()`) |
| `ZeroAlloc.Saga.Outbox.Redis` | Outbox rows committed in the Redis saga store's `MULTI/EXEC` (`WithRedisOutbox()`) |
| `ZeroAlloc.Saga.Resilience` | Resilience policies around step dispatch (`WithResilience()`) |

The base `ZeroAlloc.Saga` package contains both the runtime and the source
generator (bundled as an analyzer asset). No separate `ZeroAlloc.Saga.Generator` package to
install.

`IMediator` and the handler dispatch are emitted by the ZeroAlloc.Mediator source generator, which
NuGet does not flow through `ZeroAlloc.Saga`'s dependency on `ZeroAlloc.Mediator`. Reference it
directly in the project that declares your sagas, or the generated saga code fails to compile with
`CS0246: The type or namespace name 'IMediator' could not be found`:

```bash
dotnet add package ZeroAlloc.Mediator.Generator
```

Version floors for the bridges: `ZeroAlloc.Saga.Outbox` requires `ZeroAlloc.Outbox` 4.4.3 or later
and `ZeroAlloc.Serialisation` 2.8.1 or later, `ZeroAlloc.Saga.Resilience` requires
`ZeroAlloc.Resilience` 3.3 or later, and `ZeroAlloc.Saga.Redis` requires `StackExchange.Redis` 3.4 or
later. EF Core users of the outbox bridge also need the matching 4.x `ZeroAlloc.Outbox.EfCore`.

Hard dependencies pulled in transitively:

- `ZeroAlloc.Mediator` — for `INotification`, `IRequest`, `IMediator.Send`
- `Microsoft.Extensions.DependencyInjection` — for `AddSaga()`, `WithXxxSaga()`
- `Microsoft.Extensions.Logging.Abstractions` — for the saga-handler loggers
