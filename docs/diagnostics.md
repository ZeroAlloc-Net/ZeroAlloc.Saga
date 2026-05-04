# Diagnostics

`ZeroAlloc.Saga` ships source-generator diagnostics that catch authoring
mistakes at compile time. Code-fix providers ship with several so the IDE
can apply the fix in-place.

The IDE help-link button on each squiggle takes you to the corresponding
section below. Range `ZASAGA001`-`ZASAGA099` is reserved for Saga core; future
backend bridges (EfCore, Redis, etc.) get `ZASAGA1xx`, `ZASAGA2xx`, …

## Severity legend

- `error` — generator output won't compile if not fixed.
- `warning` — generator output compiles but the design is suspicious.

| ID | Severity | Code-fix |
|---|---|---|
| [ZASAGA001](#zasaga001) | error | `Make partial` |
| [ZASAGA002](#zasaga002) | error | — |
| [ZASAGA003](#zasaga003) | error | — |
| [ZASAGA004](#zasaga004) | error | — |
| [ZASAGA005](#zasaga005) | error | — |
| [ZASAGA006](#zasaga006) | error | — |
| [ZASAGA007](#zasaga007) | error | `Renumber steps` |
| [ZASAGA008](#zasaga008) | error | — |
| [ZASAGA009](#zasaga009) | error | `Add compensation method` |
| [ZASAGA010](#zasaga010) | error | — |
| [ZASAGA011](#zasaga011) | warning | — |
| [ZASAGA012](#zasaga012) | warning | — |
| [ZASAGA013](#zasaga013) | warning | — |
| [ZASAGA018](#zasaga018) | warning (suppressible) | — |

---

## ZASAGA001

**`[Saga] class must be partial`**

The generator emits a partial-class completion for every `[Saga]` type.
The class must therefore be declared `partial`.

```csharp
// ❌
[Saga] public class OrderSaga { /* ... */ }

// ✅
[Saga] public partial class OrderSaga { /* ... */ }
```

A code-fix provider applies the `partial` modifier in-place.

## ZASAGA002

**`[Saga] class has unsupported shape`**

Sagas must be **non-static, non-abstract, non-generic, top-level** types.

| Shape | Why rejected |
|---|---|
| `static class` | No instances to load. |
| `abstract class` | `new TSaga()` requires a concrete type. |
| `class Saga<T>` | Generators don't have the closed-type bindings to emit handlers. |
| Nested classes | DI registrations and handler types don't survive nesting cleanly. |

## ZASAGA003

**`[Saga] class lacks an accessible parameterless constructor`**

`InMemorySagaStore<TSaga, TKey>` (and future durable stores) instantiate
sagas via `new TSaga()`. Add a `public` or `internal` parameterless
constructor.

```csharp
// ❌
[Saga] public partial class OrderSaga
{
    public OrderSaga(IClock clock) { /* ... */ }
}

// ✅
[Saga] public partial class OrderSaga
{
    public OrderSaga() { }
    public OrderSaga(IClock clock) { /* ... */ }
}
```

## ZASAGA004

**`[Step] input event has no [CorrelationKey] method`**

Every step's input event type must be mapped to the saga's correlation
key by a `[CorrelationKey]` method on the same saga class.

```csharp
[Step(Order = 1)] public NextCommand DoStep(StepEvent e) => new(/* ... */);
// ❌ missing: [CorrelationKey] public OrderId Correlation(StepEvent e) => e.OrderId;
```

## ZASAGA005

**`[CorrelationKey] methods return inconsistent types`**

A saga has exactly one correlation key type. All `[CorrelationKey]`
methods within the saga must return the same type.

```csharp
// ❌
[CorrelationKey] public OrderId Correlation(OrderPlaced e)   => e.OrderId;
[CorrelationKey] public Guid    Correlation(StockReserved e) => e.RawId;

// ✅
[CorrelationKey] public OrderId Correlation(OrderPlaced e)   => e.OrderId;
[CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
```

## ZASAGA006

**`[CorrelationKey] method has wrong signature`**

A `[CorrelationKey]` method must have the shape `TKey M(TEvent e)`
exactly. No multiple parameters, no `void` return.

## ZASAGA007

**`[Step(Order = ...)] values have gaps or duplicates`**

Step `Order` values must form the contiguous sequence `1, 2, 3, ...` so
the generated FSM has a deterministic forward path.

```csharp
// ❌ gaps + duplicate
[Step(Order = 1)] /* ... */
[Step(Order = 3)] /* ... */
[Step(Order = 3)] /* ... */
```

A code-fix provider renumbers the steps to a contiguous sequence in
declaration order.

## ZASAGA008

**`[Step] method has wrong signature`**

A `[Step]` method must have the shape `TCommand M(TEvent e)`. The
return type must be a command (something the user dispatches via
`IMediator.Send`); the parameter must be exactly one event.

## ZASAGA009

**`[Step.Compensate] target is missing or mis-shaped`**

`Compensate = nameof(X)` must point at a parameterless method on the
saga returning the compensation command.

```csharp
// ❌ method missing
[Step(Order = 1, Compensate = nameof(CancelReservation))]
public ReserveStockCommand ReserveStock(OrderPlaced e) => /* ... */;
// missing: public CancelReservationCommand CancelReservation() => new(OrderId);

// ❌ wrong shape (must be parameterless, must return a command)
public void CancelReservation(OrderId id) { /* ... */ }
```

A code-fix provider stubs out the missing compensation method with a
matching signature.

## ZASAGA010

**`[Step.CompensateOn] event has no [CorrelationKey]`**

When a step uses `CompensateOn = typeof(X)`, event `X` must also have a
`[CorrelationKey]` method on the saga so the framework can locate the
right saga instance when `X` arrives.

## ZASAGA011

**`[CorrelationKey] method appears to mutate state`** (warning)

Correlation key extraction runs through a **shared probe instance** —
the generator allocates one per saga class and reuses it for every
inbound event. Mutating fields or calling state setters on that probe
breaks every other saga instance.

```csharp
// ⚠ ZASAGA011
[CorrelationKey] public OrderId Correlation(OrderPlaced e)
{
    OrderId = e.OrderId;     // mutates the shared probe
    return e.OrderId;
}

// ✅
[CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
```

This is a heuristic syntax check — it catches obvious assignment
mutations but won't catch e.g. method calls into other types that
themselves mutate state. Treat it as best-effort and keep
`[CorrelationKey]` bodies to a `=> e.X` style expression body. State
that needs to persist should be assigned **inside `[Step]` methods**
where the loaded saga instance is the actual target.

## ZASAGA012

**`Step has Compensate but no CompensateOn — dead code`** (warning)

A step with `Compensate` but no `CompensateOn` declares a compensation
method that the framework will never invoke automatically. It can still
be triggered via `ISagaManager.CompensateAsync`, but if the design
intends automatic rollback, you probably forgot the `CompensateOn`.

```csharp
// ⚠ ZASAGA012
[Step(Order = 1, Compensate = nameof(CancelReservation))]
public ReserveStockCommand ReserveStock(OrderPlaced e) => /* ... */;
```

Either add the failure event:

```csharp
[Step(Order = 1, Compensate = nameof(CancelReservation), CompensateOn = typeof(ReservationFailed))]
```

or remove the `Compensate` if manual-only compensation is intended.

## ZASAGA013

**`Two [Saga] classes correlate on same event with different key types`** (warning)

Two `[Saga]` classes correlate on the same event but use different
correlation key types — usually a sign the design is muddled. Use
composite key types or a shared key alias to make the intent explicit.

```csharp
// ⚠ ZASAGA013
[Saga] public partial class OrderSaga
{
    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
}

[Saga] public partial class AuditSaga
{
    [CorrelationKey] public Guid Correlation(OrderPlaced e) => e.AuditId;
}
```

## ZASAGA018

**`Add{Saga}Saga() is renamed; use With{Saga}Saga() instead`** (warning, suppressible)

The generator-emitted per-saga registration method now lives under the
`With`-prefixed name to align with the rest of the builder API
(`WithEfCoreStore`, `WithOutbox`, `WithResilience`). The legacy
`Add{Saga}Saga()` shim still compiles but emits this diagnostic. The
shim will be removed in v2.

```csharp
// ⚠ ZASAGA018
services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .AddOrderFulfillmentSaga();   // <-- replace with WithOrderFulfillmentSaga()

// ✓ migrated
services.AddSaga()
    .WithEfCoreStore<AppDbContext>()
    .WithOrderFulfillmentSaga();
```

To suppress during migration: add `ZASAGA018` to your csproj's
`<NoWarn>` or use `#pragma warning disable ZASAGA018` at the call site.

## See also

- [`concepts.md`](concepts.md) — overall saga lifecycle
- [`correlation.md`](correlation.md) — `[CorrelationKey]` deep-dive
- [`compensation.md`](compensation.md) — `Compensate` / `CompensateOn` deep-dive
