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

For every `[Saga]` class the generator emits the files below. Once per assembly it also emits
`MediatorSagaCommandDispatcher`, which sends that assembly's saga commands through its
`IMediator`, and `GeneratedSagaCommandSource`, which lists them; see
[Sagas in more than one assembly](#sagas-in-more-than-one-assembly).

1. **`<Saga>.Fsm.g.cs`** — a companion state machine modeling the steps as
   FSM states (`NotStarted` → `Step1` → … → `Completed`; or `Compensating` →
   `Compensated`). Used to enforce step ordering at runtime.
2. **`<Saga>.g.cs`** — a tiny partial-class completion that exposes the
   FSM as a property on the saga instance.
3. **`<Saga>.Handler.<Event>.g.cs`** — one
   `INotificationHandler<TEvent>` per event the saga subscribes to. The
   handler acquires the per-saga lock, loads (or creates) the saga, advances
   the FSM via `TryFire`, invokes the user step, dispatches the returned
   command, saves, and releases the lock. For failure events tagged with
   `CompensateOn`, the handler dispatches the reverse-cascade compensation
   chain.
4. **`<Saga>.CorrelationDispatch.g.cs`** — a static helper that calls
   the user's `[CorrelationKey]` methods through a single shared probe
   instance.
5. **`<Saga>.BuilderExtensions.g.cs`** — the `WithXxxSaga()` extension
   method that registers every concrete-closed-type the saga needs. AOT-safe;
   nothing is resolved with open generics at runtime.
6. **`<Saga>.PersistableState.g.cs`** — the saga's `ISagaPersistableState` implementation,
   `Snapshot()` and `Restore()`, which durable stores use to save and load its state.

`<Saga>` and `<Event>` are the saga's and the event's full names: the namespace, then any
containing types joined by `+`, each with its generic arity, for example
`Shop.OrderSaga.Handler.Shop.OrderPlaced.g.cs`. Sagas or events with the same name in
different namespaces therefore get their own files. File names are not a contract and may
change between releases.

Inside those files, an event's handler class is `<Saga>_<Event>_Handler` and its FSM trigger is
`Trigger.<Event>`, where `<Event>` is the event's simple name, such as `OrderPlaced`. When two
events of one saga have the same simple name, such as `Warehouse.Placed` and `Billing.Placed`,
both use their full name with each `.` replaced by `_` instead: `OrderSaga_Warehouse_Placed_Handler`
and `Trigger.Warehouse_Placed`. Each event then keeps its own handler and trigger. The handler
class name appears as the logger category in log output.

## Sagas in more than one assembly

Sagas can live in several projects of one application: for example `Billing` declares
`InvoiceSaga` and `Shipping` declares `ShipmentSaga`. Register each with its generated extension
on the same builder, in any order:

```csharp
services.AddSaga()
    .WithInvoiceSaga()       // from Billing
    .WithShipmentSaga();     // from Shipping
```

ZeroAlloc.Mediator emits an internal `IMediator` into every compilation, so only code inside
`Billing` can send `Billing`'s commands. The generator therefore emits a
`GeneratedSagaCommandSource` into each assembly with sagas. It lists that assembly's step and
compensation command types and dispatches them through the assembly's own mediator. Every
`With{Saga}()` adds its assembly's source to the service collection; adding it twice is a
no-op. No assembly is scanned, so it works under native AOT and whether or not an assembly has
been loaded yet.

- **Default dispatch.** With one source, `ISagaCommandDispatcher` is that assembly's
  `MediatorSagaCommandDispatcher`, as it always was. With more, it routes each command to the
  source that lists its type.
- **Outbox dispatch.** `WithOutbox()` registers an outbox dispatcher for every command type of
  every source, including sources added after it. Each assembly with sagas must reference
  `ZeroAlloc.Serialisation`; the start check names any that does not. See [`outbox.md`](outbox.md).
- **One owner per command type.** A command type returned by sagas in two assemblies is
  rejected when the second `With{Saga}()` runs, with an `InvalidOperationException` that names
  both assemblies. The outbox keeps one dispatcher per type name, so two owners cannot both be
  served. Give each assembly's sagas their own command types.
- **Rebuild saga assemblies together.** A saga assembly compiled by an older Saga generator
  does not register a source. Rebuild it against the same ZeroAlloc.Saga version as the
  application.

Integrations that register something per saga command, as the outbox bridge does, use
`ISagaBuilder.ForEachCommandSource(...)`. It runs a callback for every source already added and
every source added later.

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
