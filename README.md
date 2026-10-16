# ZeroAlloc.Saga

[![NuGet](https://img.shields.io/nuget/v/ZeroAlloc.Saga.svg)](https://www.nuget.org/packages/ZeroAlloc.Saga)
[![Build](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/actions/workflows/build.yml/badge.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/LICENSE)
[![AOT](https://img.shields.io/badge/AOT--Compatible-passing-brightgreen)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/MarcelRoozekrans?style=flat&logo=githubsponsors&color=ea4aaa&label=Sponsor)](https://github.com/sponsors/MarcelRoozekrans)

Source-generated sagas for .NET: declare steps and their compensation on a partial class, and get a state machine with no reflection that runs under NativeAOT, on in-memory, EF Core, ZeroAlloc.ORM or Redis storage.

## Why ZeroAlloc.Saga

- **A workflow is one partial class.** Steps, correlation keys and compensation are attributes. The generator emits the state machine, the event handlers and the command dispatch, and compensation runs in reverse automatically when a failure event arrives.
- **Works under NativeAOT and trimming.** CI publishes and runs AOT samples for the core, the outbox bridge, the ORM store and the resilience bridge. The EF Core store runs on JIT hosts only, blocked upstream.
- **Compile-time diagnostics.** 16 generator diagnostics catch authoring mistakes such as gaps in step order, missing correlation keys or a misshaped compensation method, three of them with code fixes.
- **No double dispatch under retry.** With the outbox bridge on the EF Core, ORM or Redis store, each step command is committed atomically with the saga state, so an optimistic-concurrency retry cannot send a command twice.

## Install

```bash
dotnet add package ZeroAlloc.Saga
```

`ZeroAlloc.Saga` carries its own generator and brings ZeroAlloc.Mediator, whose generator emits the `IMediator` that saga commands and events flow through. One package reference is enough.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

var services = new ServiceCollection().AddTransient<Warehouse>().AddTransient<Payments>();
services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)); // a host app's AddLogging() covers this
services.AddSaga().WithCheckoutSaga(); // generated for the [Saga] class
await using var provider = services.BuildServiceProvider();
var mediator = provider.GetRequiredService<IMediator>();

await mediator.Publish(new OrderPlaced(42, 9.95m), default); // step 1 sends ReserveStock
await mediator.Publish(new StockReserved(42), default);      // step 2 sends ChargeCard
await mediator.Publish(new PaymentDeclined(42), default);    // compensation sends ReleaseStock

public sealed record OrderPlaced(int OrderId, decimal Total) : INotification;
public sealed record StockReserved(int OrderId) : INotification;
public sealed record PaymentCharged(int OrderId) : INotification;
public sealed record PaymentDeclined(int OrderId) : INotification;

public readonly record struct ReserveStock(int OrderId) : IRequest;
public readonly record struct ReleaseStock(int OrderId) : IRequest;
public readonly record struct ChargeCard(int OrderId, decimal Total) : IRequest;
public readonly record struct ShipOrder(int OrderId) : IRequest;

[Saga]
public partial class CheckoutSaga
{
    public int OrderId { get; private set; }
    public decimal Total { get; private set; }

    [CorrelationKey] public int Key(OrderPlaced e) => e.OrderId;
    [CorrelationKey] public int Key(StockReserved e) => e.OrderId;
    [CorrelationKey] public int Key(PaymentCharged e) => e.OrderId;
    [CorrelationKey] public int Key(PaymentDeclined e) => e.OrderId;

    [Step(Order = 1, Compensate = nameof(Release))]
    public ReserveStock Reserve(OrderPlaced e) { (OrderId, Total) = (e.OrderId, e.Total); return new(OrderId); }

    [Step(Order = 2, CompensateOn = typeof(PaymentDeclined))]
    public ChargeCard Charge(StockReserved e) => new(OrderId, Total);

    [Step(Order = 3)]
    public ShipOrder Ship(PaymentCharged e) => new(OrderId);

    public ReleaseStock Release() => new(OrderId);
}

public sealed class Warehouse : IRequestHandler<ReserveStock, Unit>, IRequestHandler<ReleaseStock, Unit>, IRequestHandler<ShipOrder, Unit>
{
    public ValueTask<Unit> Handle(ReserveStock c, CancellationToken ct) => Log($"Reserved stock for {c.OrderId}");
    public ValueTask<Unit> Handle(ReleaseStock c, CancellationToken ct) => Log($"Released stock for {c.OrderId}");
    public ValueTask<Unit> Handle(ShipOrder c, CancellationToken ct) => Log($"Shipped {c.OrderId}");
    internal static ValueTask<Unit> Log(string what) { Console.WriteLine(what); return new(Unit.Value); }
}

public sealed class Payments : IRequestHandler<ChargeCard, Unit>
{
    public ValueTask<Unit> Handle(ChargeCard c, CancellationToken ct) => Warehouse.Log($"Charging {c.Total}");
}
```

It prints `Reserved stock for 42`, `Charging 9.95` and `Released stock for 42`. A saga that reaches a terminal state is removed from the store automatically. [Concepts](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/concepts.md) explains the lifecycle and what the generator emits.

## What you get

- Correlation by any key type, including strongly typed IDs and composite keys.
- Automatic compensation on a failure event, and manual compensation through `ISagaManager.CompensateAsync`.
- Optimistic concurrency with retry on conflict, each attempt in a fresh DI scope.
- In-memory storage by default; switch to a durable store with one fluent call.

Optional packages:

- [`ZeroAlloc.Saga.EfCore`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/persistence-efcore.md): durable store on your EF Core `DbContext`, with row-version concurrency.
- [`ZeroAlloc.Saga.Orm`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md#zeroallocsagaorm-atomic-with-withormoutbox): durable store over ZeroAlloc.ORM, NativeAOT-ready.
- [`ZeroAlloc.Saga.Redis`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/persistence-redis.md): durable store on Redis with `WATCH/MULTI/EXEC` concurrency.
- [`ZeroAlloc.Saga.Outbox`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md): step commands dispatched through ZeroAlloc.Outbox, committed with the saga state.
- [`ZeroAlloc.Saga.Outbox.Orm`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md#zeroallocsagaorm-atomic-with-withormoutbox): outbox rows in the ORM saga store's transaction.
- [`ZeroAlloc.Saga.Outbox.Redis`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox-redis.md): outbox rows in the Redis saga store's `MULTI/EXEC`.
- [`ZeroAlloc.Saga.Resilience`](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/resilience.md): retry, timeout, circuit-breaker and rate-limit around step dispatch.

## Performance

Saga state is persisted through generated `Snapshot()` and `Restore()` methods. Exact budgets, asserted by `SagaStateAllocationTests`:

| Call | Allocation |
|---|---:|
| `SagaStateWriter`, every supported shape | 0 B |
| Generated `Restore()` | 0 B |
| Generated `Snapshot()`, a 21-byte state | 48 B, the returned array |

There is no published head-to-head against another saga library yet. Details: [docs/performance.md](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/performance.md).

## Learn more

- Documentation site: [saga.zeroalloc.net](https://saga.zeroalloc.net)
- Documentation source: [docs/](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/tree/main/docs), starting with [concepts](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/concepts.md), [correlation](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/correlation.md) and [compensation](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/compensation.md)
- Reference: [packages and dependencies](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/packages.md), [diagnostics](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/diagnostics.md), [samples](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/samples.md), [limitations and roadmap](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/roadmap.md)
- Upgrading: [migrating to v4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/migrating-to-v4.md), [release highlights](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/release-highlights.md), [CHANGELOG](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/CHANGELOG.md)
- Related: [ZeroAlloc.Mediator](https://github.com/ZeroAlloc-Net/ZeroAlloc.Mediator), [ZeroAlloc.Outbox](https://github.com/ZeroAlloc-Net/ZeroAlloc.Outbox), [ZeroAlloc.Resilience](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience), [ZeroAlloc.EventSourcing](https://github.com/ZeroAlloc-Net/ZeroAlloc.EventSourcing)
- Contributing: issues and pull requests are welcome on [GitHub](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/issues)
- License: MIT, see [LICENSE](https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/LICENSE)
