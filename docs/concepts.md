# Concepts

`ZeroAlloc.Saga` is a source-generated orchestration library for long-running,
event-driven business processes. A *saga* coordinates a sequence of steps,
each driven by an incoming notification, that together implement a
business transaction. When something goes wrong partway through, the saga
runs the *compensation* path in reverse to undo the prior side effects.

## The four moving parts

```
event(notification) ─► [INotificationHandler<T>] ─► [Saga step] ─► command(IRequest)
                                                                       │
                                                                       ▼
                                                                IMediator.Send
```

| Term | What it is |
|---|---|
| **Saga class** | A `partial class` annotated with `[Saga]`. Holds per-instance state as fields/properties and declares step + compensation methods. |
| **Step** | A method annotated with `[Step(Order = N)]`. Takes a notification event, returns the next command to dispatch. |
| **Correlation key** | A method annotated with `[CorrelationKey]` per event type. Returns the strongly-typed identifier (e.g. `OrderId`) that ties events to a saga instance. |
| **Compensation** | A method that produces an "undo" command. Wired to a step via `Compensate = nameof(...)` and triggered automatically by `CompensateOn = typeof(FailureEvent)` or operationally via `ISagaManager.CompensateAsync`. |

## Example

```csharp
[Saga]
public partial class OrderFulfillmentSaga
{
    public OrderId OrderId { get; private set; }
    public decimal Total { get; private set; }

    [CorrelationKey] public OrderId Correlation(OrderPlaced e)     => e.OrderId;
    [CorrelationKey] public OrderId Correlation(StockReserved e)   => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentCharged e)  => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;

    [Step(Order = 1, Compensate = nameof(CancelReservation))]
    public ReserveStockCommand ReserveStock(OrderPlaced e)
    {
        OrderId = e.OrderId; Total = e.Total;
        return new ReserveStockCommand(e.OrderId, e.Total);
    }

    [Step(Order = 2, Compensate = nameof(RefundPayment), CompensateOn = typeof(PaymentDeclined))]
    public ChargeCustomerCommand ChargeCustomer(StockReserved e) => new(OrderId, Total);

    [Step(Order = 3)]
    public ShipOrderCommand ShipOrder(PaymentCharged e) => new(OrderId);

    public CancelReservationCommand CancelReservation() => new(OrderId);
    public RefundPaymentCommand RefundPayment() => new(OrderId);
}
```

Wiring:

```csharp
services.AddMediator();              // ZeroAlloc.Mediator
services.AddSaga()
    .WithOrderFulfillmentSaga();      // generator-emitted extension
```

## What the source generator emits

For every `[Saga]` class the generator emits five files:

1. **`<SagaName>Fsm.g.cs`** — a companion state machine modeling the steps as
   FSM states (`NotStarted` → `Step1` → … → `Completed`; or `Compensating` →
   `Compensated`). Used to enforce step ordering at runtime.
2. **`<SagaName>.g.cs`** — a tiny partial-class completion that exposes the
   FSM as a property on the saga instance.
3. **`<SagaName>_<EventType>_Handler.g.cs`** — one
   `INotificationHandler<TEvent>` per event the saga subscribes to. The
   handler acquires the per-saga lock, loads (or creates) the saga, advances
   the FSM via `TryFire`, invokes the user step, dispatches the returned
   command, saves, and releases the lock. For failure events tagged with
   `CompensateOn`, the handler dispatches the reverse-cascade compensation
   chain.
4. **`<SagaName>CorrelationDispatch.g.cs`** — a static helper that calls
   the user's `[CorrelationKey]` methods through a single shared probe
   instance.
5. **`<SagaName>BuilderExtensions.g.cs`** — the `AddXxxSaga()` extension
   method that registers every concrete-closed-type the saga needs. AOT-safe;
   nothing is resolved with open generics at runtime.

## Lifecycle

| Event | Behaviour |
|---|---|
| Step 1 event arrives | Saga is auto-created (`LoadOrCreateAsync`). |
| Step N>1 event arrives, no saga | Saga is created but stays in `NotStarted`; the event is rejected by `TryFire` and silently ignored (logged at Debug). |
| Same Step 1 event arrives twice | Second one no-ops because the FSM has already advanced past `NotStarted`. |
| Final step completes | FSM reaches a terminal state (`Completed` or `Compensated`); the saga is removed from the store. |
| Failure event with `CompensateOn` | Reverse-cascade compensation fires; FSM transitions through `Compensating` → `Compensated`; saga removed. |
| Orphan failure event | Logged at Warning; no commands dispatched. |

## Concurrency

The framework uses a per-saga `SemaphoreSlim` keyed by correlation ID so
that handlers for the same saga instance never run concurrently. Different
saga instances run in parallel — there's no global lock. See `SagaLockManager<TKey>`.

## Persistence

`v1.0` ships `InMemorySagaStore<TSaga, TKey>`. Saga instances are held by
reference in a `ConcurrentDictionary`; mutations are visible immediately
to subsequent loads, so `SaveAsync` is a no-op. **InMemory is not durable** —
process crash loses all in-flight sagas. Durable stores
(`ZeroAlloc.Saga.EfCore`, `ZeroAlloc.Saga.Redis`) ship in v1.1.

## Native AOT

The runtime is fully AOT-compatible: no reflection, no open-generic resolution
at runtime, no dynamic code paths. The generator emits concrete-closed-type
DI registrations so the AOT compiler can statically reach every type pair.
A CI smoke test (`samples/AotSmoke/`) publishes with `PublishAot=true` and
asserts the saga reaches `Completed` and is removed from the store.

## Further reading

- [`correlation.md`](correlation.md) — how `[CorrelationKey]` works, ZASAGA011 purity warnings, multi-saga subscription
- [`compensation.md`](compensation.md) — `Compensate` / `CompensateOn`, the reverse cascade, manual compensation via `ISagaManager`
- [`diagnostics.md`](diagnostics.md) — every ZASAGA0XX diagnostic, with examples and fixes
