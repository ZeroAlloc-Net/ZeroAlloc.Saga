# OrderFulfillment sample

A console-driven walkthrough of `ZeroAlloc.Saga` showing the four canonical
flows of a 3-step order saga.

## What it demonstrates

| Scenario | Flow |
|---|---|
| 1 | Happy path — `OrderPlaced` → `ReserveStock` → `StockReserved` → `ChargeCustomer` → `PaymentCharged` → `ShipOrder` → saga reaches `Completed` and is removed. |
| 2 | Compensation — payment declined; reverse cascade fires `RefundPayment` then `CancelReservation`; saga is `Compensated` and removed. |
| 3 | Orphan failure — `PaymentDeclined` arrives with no matching saga; logged and ignored. |
| 4 | Operator-initiated compensation — saga is parked at Step 1 and the operator calls `ISagaManager.CompensateAsync`. |

## Run

```bash
# Default: in-memory store
dotnet run --project samples/OrderFulfillment/

# Or wire the EfCore + SQLite backend (creates ./saga-demo.db)
dotnet run --project samples/OrderFulfillment/ -- --efcore
```

Each scenario prints its commands inline so the saga's behaviour is visible.
Both backends should produce identical demo output — the EfCore variant
additionally persists each saga to a SQLite file (deleted on each launch
so the demo always starts fresh).

## How the wiring works

The sample uses the real ZeroAlloc.Mediator, with an `IRequestHandler` per step
command in `Handlers/CommandHandlers.cs`. Each handler hands the command to a
`FakeMediator` demo object, which prints it and queues the next event, such as
`StockReserved` after `ReserveStock`. A `SagaDriver` publishes that queued event
once the current saga handler has returned. That self-driving loop is what lets
a single console app exercise the entire orchestration without any external
services.

In a real system the command handlers do the actual work and publish the next
`INotification` themselves, or leave it to an outbox or message bus. The saga
itself doesn't change between the demo and production — only the command-side
handlers do.
