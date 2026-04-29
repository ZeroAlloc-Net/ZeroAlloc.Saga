# Compensation

A saga that can't roll back partial work isn't really a saga — it's just
a multi-step request. `ZeroAlloc.Saga` makes compensation a first-class
concern: every step that produces a side effect declares the command
that undoes it, and a single failure event triggers the whole reverse
cascade.

## Two triggers, one mechanism

| Trigger | When |
|---|---|
| `[Step(CompensateOn = typeof(FailureEvent))]` | Automatic — the saga subscribes to `FailureEvent` and runs the cascade when it arrives. |
| `await ISagaManager<TSaga, TKey>.CompensateAsync(key, ct)` | Manual — operator dashboards, admin scripts, or higher-level workflows can trigger compensation explicitly. |

Both paths share the same generator-emitted cascade switch. Whatever the
trigger, the same compensating commands run in the same order.

## Declaring compensation

```csharp
[Step(Order = 1, Compensate = nameof(CancelReservation))]
public ReserveStockCommand ReserveStock(OrderPlaced e) { /* ... */ }

[Step(Order = 2, Compensate = nameof(RefundPayment), CompensateOn = typeof(PaymentDeclined))]
public ChargeCustomerCommand ChargeCustomer(StockReserved e) { /* ... */ }

[Step(Order = 3)]
public ShipOrderCommand ShipOrder(PaymentCharged e) { /* ... */ }

public CancelReservationCommand CancelReservation() => new(OrderId);
public RefundPaymentCommand RefundPayment() => new(OrderId);
```

Rules:

| Rule | Diagnostic |
|---|---|
| `Compensate = nameof(X)` references a method on the saga class returning an `IRequest`. | `ZASAGA009` |
| `CompensateOn = typeof(X)` references an event type that has a matching `[CorrelationKey]` method. | `ZASAGA010` |
| A step with `Compensate` but no `CompensateOn` is allowed but warns — that compensation method is dead code unless triggered manually. | `ZASAGA012` |

Compensation methods take **no parameters**. They use the saga's
captured state (set during the forward step) to build the undo command.
That's why step methods normally assign the relevant fields:

```csharp
public ReserveStockCommand ReserveStock(OrderPlaced e)
{
    OrderId = e.OrderId;     // captured for the compensation path
    Total = e.Total;
    return new ReserveStockCommand(e.OrderId, e.Total);
}

public CancelReservationCommand CancelReservation() => new(OrderId);
```

## The reverse cascade

When compensation fires, the framework walks the chain in **reverse
declaration order** starting from the most recent forward step. Earlier
steps are skipped if the saga never reached them.

For the example above, with the saga at Step 2 when `PaymentDeclined`
arrives:

1. Capture current FSM state (= `Step2`).
2. Transition to `Compensating`.
3. For Step 2: dispatch `RefundPayment()`.
4. For Step 1: dispatch `CancelReservation()`.
5. Transition to `Compensated`.
6. Remove the saga from the store.

The cascade is generator-emitted code (the FSM doesn't model action
callbacks, only state transitions), so each saga's compensation chain
is a small bespoke switch:

```csharp
switch (stateAtFailure)
{
    case OrderFulfillmentSagaFsm.State.Step2:
        await _mediator.Send(saga.RefundPayment(), ct);
        await _mediator.Send(saga.CancelReservation(), ct);
        break;
    case OrderFulfillmentSagaFsm.State.Step1:
        await _mediator.Send(saga.CancelReservation(), ct);
        break;
}
```

## Manual compensation

`ISagaManager<TSaga, TKey>.CompensateAsync` exposes the same cascade as
an operations API. Useful when:

- An external system reports a failure that doesn't surface as one of
  the saga's `CompensateOn` events.
- An operator decides to abort a workflow that's parked waiting for
  the next event.
- A timeout policy outside the saga (v1.3+) wants to trigger compensation.

```csharp
public sealed class OrderAdminController(ISagaManager<OrderFulfillmentSaga, OrderId> sagas)
{
    public Task AbortOrderAsync(OrderId id, CancellationToken ct)
        => sagas.CompensateAsync(id, ct).AsTask();
}
```

Manual compensation is a no-op if the saga doesn't exist (e.g. it's
already completed or compensated).

## Orphan failure events

A failure event with no matching saga is logged at `Warning` level and
otherwise ignored. No commands are dispatched. This matches MassTransit
and NServiceBus defaults — failure events should always be safe to
deliver, even if the original saga is gone.

```
warn: ZeroAlloc.Saga.OrderFulfillmentSaga_PaymentDeclined_Handler[0]
      Orphan PaymentDeclined for Order#42
```

## Compensation idempotency is your responsibility

The framework guarantees:

- The cascade runs at most once per saga instance.
- Compensating commands run in reverse declaration order.
- The saga is removed from the store after the cascade completes.

The framework does **not** guarantee:

- That every dispatched compensating command actually succeeded
  (commands return `Unit`, not a status).
- That compensating commands are idempotent.

If your domain requires "the refund actually went through" before the
saga is considered compensated, the compensating command handler is the
right place for that check — not the saga.

## See also

- [`concepts.md`](concepts.md) — overall saga lifecycle
- [`correlation.md`](correlation.md) — `[CorrelationKey]` rules
- [`diagnostics.md`](diagnostics.md) — every ZASAGA0XX diagnostic
