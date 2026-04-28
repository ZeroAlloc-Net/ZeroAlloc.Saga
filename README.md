# ZeroAlloc.Saga

Source-generated long-running process orchestration for the ZeroAlloc ecosystem.

> **Status:** v1.0 in development — see [docs/plans/](https://github.com/ZeroAlloc-Net/ZeroAlloc.docs/blob/main/docs/plans/) for the design + implementation plan.

## Overview

`ZeroAlloc.Saga` lets you express multi-step business workflows declaratively as a partial class. The source generator emits state-machine code, notification handlers, and dispatch wiring. Compensation runs in reverse on failure.

```csharp
[Saga]
public partial class OrderFulfillmentSaga
{
    [CorrelationKey] public OrderId Correlation(OrderPlaced e)     => e.OrderId;
    [CorrelationKey] public OrderId Correlation(StockReserved e)   => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentCharged e)  => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;

    [Step(Order = 1, Compensate = nameof(CancelReservation))]
    public ReserveStockCommand ReserveStock(OrderPlaced evt)
        => new(evt.OrderId, evt.Items);

    [Step(Order = 2, Compensate = nameof(RefundPayment), CompensateOn = typeof(PaymentDeclined))]
    public ChargeCustomerCommand ChargeCustomer(StockReserved evt)
        => new(evt.OrderId, evt.Total);

    [Step(Order = 3)]
    public ShipOrderCommand ShipOrder(PaymentCharged evt) => new(evt.OrderId);

    public CancelReservationCommand CancelReservation() => new(/* ... */);
    public RefundPaymentCommand RefundPayment() => new(/* ... */);
}
```

Wiring:

```csharp
services.AddMediator();
services.AddSaga().AddOrderFulfillmentSaga();
```

Documentation: see [docs/](docs/) (added in v1.0 release).

## v1.0 known limitations

- **InMemory persistence is not durable.** Process crash loses all in-flight sagas. EfCore/Redis bridges arrive in v1.1.
- **`SagaLockManager` grows monotonically** — one `SemaphoreSlim` per unique correlation key seen, never evicted. Bounded by process lifetime; ~80 bytes each. Eviction lands in v1.x for high-cardinality workloads.
- **No timeouts.** v1.0 sagas wait indefinitely for the next event. Phase 4 (v1.3) adds `[Step(TimeoutMs = ...)]` via Scheduling integration.
- **No telemetry.** v1.0 emits no spans, counters, or histograms. Phase 5 (v1.4) ships `ZeroAlloc.Saga.Telemetry` bridge.

## License

MIT
