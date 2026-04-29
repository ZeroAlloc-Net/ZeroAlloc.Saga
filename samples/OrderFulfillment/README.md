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
dotnet run --project samples/OrderFulfillment/
```

Each scenario prints its commands inline so the saga's behaviour is visible.

## How the wiring works

The sample uses a `FakeMediator` that, on each command it receives, prints the
command and (optionally) publishes the next event back through the saga
handlers. That self-driving loop is what lets a single console app exercise
the entire orchestration without any external services.

In a real system you would:

1. Register normal `IRequestHandler<TCommand>` instances behind `IMediator.Send`.
2. Have those handlers do their work and publish an `INotification` for the next
   step (or arrange for an outbox / message bus to do so).

The saga itself doesn't change between the demo and production — only the
command-side handlers do.

## Note on `IMediator` partial

The `IMediatorPartial.cs` file hand-rolls the typed `Send` overloads the saga
generator emits calls to. Normally those overloads are emitted by
`ZeroAlloc.Mediator.Generator`. The published 2.0.x generator nupkg shipped its
analyzer DLL under `lib/` instead of `analyzers/dotnet/cs/` (fixed in 2.0.1),
which is why this sample hand-rolls the surface area. Once 2.0.1+ is the
resolved version on this repo's central package pin, that partial can be
deleted and the generator package referenced directly.
