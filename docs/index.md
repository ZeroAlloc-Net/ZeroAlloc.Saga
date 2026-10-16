---
id: index
title: ZeroAlloc.Saga
slug: /
description: Source-generated sagas for .NET. Declare steps and their compensation on a partial class and get a reflection-free state machine that runs under NativeAOT.
sidebar_position: 1
---

# ZeroAlloc.Saga

Source-generated sagas for .NET: declare steps and their compensation on a partial class, and get a
state machine with no reflection that runs under NativeAOT, on in-memory, EF Core, ZeroAlloc.ORM or
Redis storage.

## Install

```bash
dotnet add package ZeroAlloc.Saga
dotnet add package ZeroAlloc.Mediator.Generator
```

`ZeroAlloc.Saga` carries its own generator. Saga commands and events flow through
ZeroAlloc.Mediator, whose generator you reference directly. [Packages](packages.md) lists the
optional stores and bridges.

## Quick start

```csharp
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

var services = new ServiceCollection().AddLogging().AddTransient<Warehouse>().AddTransient<Payments>();
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

It prints `Reserved stock for 42`, `Charging 9.95` and `Released stock for 42`. A saga that reaches a terminal state is removed from the store automatically. [Concepts](concepts.md) explains the lifecycle and what the generator emits.

## Contents

| Page | What it covers |
|---|---|
| [Concepts](concepts.md) | The saga lifecycle and what the generator emits |
| [Correlation](correlation.md) | Routing each event to its saga instance with `[CorrelationKey]` |
| [Compensation](compensation.md) | Rolling back completed steps on a failure event or on demand |
| [Packages](packages.md) | Install, optional packages and version floors |
| [EF Core persistence](persistence-efcore.md) | The durable store on your `DbContext` |
| [Redis persistence](persistence-redis.md) | The durable store on Redis |
| [Outbox bridge](outbox.md) | Step commands committed with the saga state |
| [Outbox on Redis](outbox-redis.md) | Atomic dispatch under the Redis store |
| [Resilience](resilience.md) | Retry, timeout, circuit-breaker and rate-limit around dispatch |
| [Diagnostics](diagnostics.md) | Every generator diagnostic and its fix |
| [Performance](performance.md) | Allocation budgets and supported state shapes |
| [Samples](samples.md) | Runnable projects, including the NativeAOT smoke tests |
| [Limitations and roadmap](roadmap.md) | What is not supported yet |
| [Migrating to v4](migrating-to-v4.md) | Upgrading the outbox bridge to ZeroAlloc.Outbox 3 |
| [Release highlights](release-highlights.md) | The what's new notes from earlier releases |
