# Correlation

Every event a saga subscribes to must be routable to a specific saga
instance. `ZeroAlloc.Saga` does this with explicit `[CorrelationKey]`
methods on the saga class — one per event type.

## Why explicit methods?

Alternatives considered and rejected:

| Approach | Why rejected |
|---|---|
| Convention (`Event.OrderId`) | Event shape pollution; couples sagas to event field names. |
| Single `Correlate` method with `dynamic` | No compile-time safety; AOT-hostile. |
| Marker interface (`ISagaEvent<TKey>`) | Requires every event to implement it, even ones used elsewhere. |

Explicit methods are **type-safe at the call site**, **AOT-friendly** (the
generator probes a shared instance), and **match MassTransit / NServiceBus**
conventions familiar to most readers.

## Anatomy

```csharp
[Saga]
public partial class OrderFulfillmentSaga
{
    [CorrelationKey] public OrderId Correlation(OrderPlaced e)     => e.OrderId;
    [CorrelationKey] public OrderId Correlation(StockReserved e)   => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentCharged e)  => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;
    /* ... */
}
```

Rules:

| Rule | Diagnostic |
|---|---|
| Method takes exactly one parameter — the event type. | `ZASAGA006` |
| Return type is the saga's correlation key (consistent across all `[CorrelationKey]` methods on the class). | `ZASAGA005` |
| Every step input event type AND every `CompensateOn` event type has a matching `[CorrelationKey]` method. | `ZASAGA004`, `ZASAGA010` |
| Method body is pure — no field assignments. | `ZASAGA011` (warning) |

The method name is by convention `Correlation`; the generator dispatches by
parameter type, not by name. You may name it `KeyOf`, `For`, etc. if your
team prefers — what matters is the `[CorrelationKey]` attribute.

## Strongly-typed keys

The correlation key must implement `IEquatable<TKey>` and be `notnull`.
Idiomatic shape: a `readonly record struct` with a single value field,
matching the [`[TypedId]`](https://github.com/ZeroAlloc-Net/) convention.

```csharp
public readonly record struct OrderId(int Value) : IEquatable<OrderId>;
```

`int`, `Guid`, `string`, etc. work too — anything that satisfies the
`notnull, IEquatable<TKey>` constraint. Using a domain-specific record
struct makes the lock manager and saga store dictionaries impossible to
mix up at the call site.

## Purity (ZASAGA011)

`[CorrelationKey]` methods are invoked many times — once per inbound
event — through a **shared probe instance** the generator allocates once
per saga type:

```csharp
internal static class OrderFulfillmentSagaCorrelationDispatch
{
    private static readonly OrderFulfillmentSaga _probe = new();
    public static OrderId GetKey(OrderPlaced e) => _probe.Correlation(e);
    /* ... */
}
```

Because that probe is shared, the body **must not mutate state**. The
generator emits a heuristic warning (`ZASAGA011`) on obvious patterns
(field assignments, property setters) but cannot prove purity in the
general case (method calls into other types, etc.). Treat ZASAGA011 as
"best-effort" and keep `[CorrelationKey]` bodies to a `=> e.X` style
expression body.

```csharp
// OK
[CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;

// Composite key — fine, still pure
[CorrelationKey] public TenantOrderId Correlation(OrderPlaced e)
    => new(e.TenantId, e.OrderId);

// ⚠ ZASAGA011 — mutating shared probe state breaks every other saga instance
[CorrelationKey] public OrderId Correlation(OrderPlaced e)
{
    OrderId = e.OrderId;          // ← captured during step methods, not here
    return e.OrderId;
}
```

State that needs to be remembered should be assigned **inside `[Step]`
methods**, where the saga instance loaded from the store is the actual
target.

## Multiple sagas, one event

Multiple `[Saga]` classes can correlate on the same event. Each saga
emits its own `INotificationHandler<TEvent>`; the framework runs all of
them. They run sequentially — order is the DI registration order.

```csharp
services.AddSaga()
    .WithOrderFulfillmentSaga()    // also subscribes to OrderPlaced
    .WithRefundSaga();             // also subscribes to OrderPlaced
```

If two sagas correlate on the same event with **different** correlation
key types, the generator emits `ZASAGA013` (warning) — usually a sign
the design is muddled. Use composite key types or a shared key alias
to make the intent explicit.

## Composite keys

Multi-tenant or hierarchical sagas often want a composite correlation key:

```csharp
public readonly record struct TenantOrderId(TenantId Tenant, OrderId Order) : IEquatable<TenantOrderId>;

[Saga]
public partial class TenantOrderSaga
{
    [CorrelationKey] public TenantOrderId Correlation(OrderPlaced e)
        => new(e.TenantId, e.OrderId);
    /* ... */
}
```

The lock manager and saga store key on the whole `TenantOrderId`, so two
orders with the same `OrderId` but different `TenantId` are independent
saga instances.
