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
ships as its own package, `ZeroAlloc.Mediator.Generator`. `ZeroAlloc.Saga` depends on it with its
analyzer assets flowing, so it runs in your project without a reference of its own. Every optional
package below depends on `ZeroAlloc.Saga` and brings both generators the same way.

The generator floor is `ZeroAlloc.Mediator.Generator` 6.6.1, the first version that dispatches every
handler interface a class implements. A project that references `ZeroAlloc.Mediator.Generator`
directly, for example to pin a newer version, still gets one generator: NuGet resolves the two
references to a single package.

Because the Mediator generator now runs in every project that references `ZeroAlloc.Saga`, such a
project gets its own internal `IMediator`, and the generator reports `ZAM001` for any `IRequest` in it
that has no handler.

Version floors for the bridges: `ZeroAlloc.Saga.Outbox` requires `ZeroAlloc.Outbox` 4.4.3 or later
and `ZeroAlloc.Serialisation` 2.8.1 or later, `ZeroAlloc.Saga.Resilience` requires
`ZeroAlloc.Resilience` 3.3 or later, and `ZeroAlloc.Saga.Redis` requires `StackExchange.Redis` 3.4 or
later. EF Core users of the outbox bridge also need the matching 4.x `ZeroAlloc.Outbox.EfCore`.

Hard dependencies pulled in transitively:

- `ZeroAlloc.Mediator` — for `INotification`, `IRequest`, `IRequestHandler`
- `ZeroAlloc.Mediator.Generator` — emits `IMediator` and its `Send`/`Publish` dispatch, as an analyzer
- `Microsoft.Extensions.DependencyInjection` — for `AddSaga()`, `WithXxxSaga()`
- `Microsoft.Extensions.Logging.Abstractions` — for the saga-handler loggers
