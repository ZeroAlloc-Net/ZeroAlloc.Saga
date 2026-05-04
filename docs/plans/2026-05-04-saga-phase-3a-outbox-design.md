# Saga Phase 3a — `ZeroAlloc.Saga.Outbox` Bridge

> Phase 3 in the Saga roadmap is two bridge packages: `Saga.Outbox` (this iteration, "Phase 3a")
> and `Saga.Resilience` (Phase 3b — separate iteration). This document covers Phase 3a only.

**Status:** Design — pending implementation
**Target version:** Saga 1.2 (core generator + abstractions) + Saga.Outbox 1.0 (new package)
**Date:** 2026-05-04

## Goal

Eliminate the documented "command may dispatch twice on OCC retry" caveat that ships with Saga 1.1
(`Saga.EfCore`) by routing saga step commands through a transactional outbox. After Phase 3a, a
saga step's command and its persisted state advance commit atomically: an OCC conflict on save
rolls back the outbox-write too, so the second attempt never observes a phantom dispatch.

## Two-package shape

| Package | Version | What changes |
|---|---|---|
| `ZeroAlloc.Saga` | 1.1.0 → 1.2.0 | New `ISagaCommandDispatcher` interface; generator emits `_dispatcher.DispatchAsync(cmd, ct)` instead of `_mediator.Send(cmd, ct)`; conditional `[ZeroAllocSerializable]` partial-extension emit when `ZeroAlloc.Serialisation` is referenced; ZASAGA016/017 diagnostics |
| `ZeroAlloc.Saga.Outbox` | new, 1.0.0 | `OutboxSagaCommandDispatcher`; `OutboxSagaCommandPoller` (`IHostedService`); `WithOutbox()` extension on the saga registration builder |

## Architecture

### Dispatcher indirection

Saga 1.2's generator replaces today's direct `IMediator.Send` with a one-method abstraction:

```csharp
namespace ZeroAlloc.Saga;

public interface ISagaCommandDispatcher
{
    ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>;
}
```

`AddSaga<MySaga>()` (existing extension) registers `MediatorSagaCommandDispatcher` as the default:

```csharp
internal sealed class MediatorSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly IMediator _mediator;
    public MediatorSagaCommandDispatcher(IMediator mediator) => _mediator = mediator;

    public ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
        => _mediator.Send(cmd, ct);
}
```

Existing Saga 1.1 users see no behavior change: the generator's runtime output is identical (`IMediator.Send` is still the actual dispatch path), just routed through one extra method call.

### Per-compilation `SagaCommandRegistry`

Saga 1.2's generator emits one merged switch table per compilation, covering every distinct
step-method return-type (and compensation return-type) across every `[Saga]` class:

```csharp
namespace ZeroAlloc.Saga.Generated;

public static class SagaCommandRegistry
{
    public static async ValueTask DispatchAsync(
        string typeName,
        ReadOnlyMemory<byte> bytes,
        IServiceProvider services,
        IMediator mediator,
        CancellationToken ct)
    {
        switch (typeName)
        {
            case "MyApp.Commands.CreateOrderCommand":
            {
                var serializer = services.GetRequiredService<ISerializer<MyApp.Commands.CreateOrderCommand>>();
                var cmd = serializer.Deserialize(bytes.Span);
                if (cmd is null)
                    throw new InvalidOperationException(
                        "ISerializer.Deserialize returned null for CreateOrderCommand. " +
                        "Outbox payload may be corrupt or the command type's serializer is misconfigured.");
                await mediator.Send(cmd, ct).ConfigureAwait(false);
                return;
            }
            // … one case per distinct command type …
            default:
                throw new InvalidOperationException(
                    $"Unknown saga command type '{typeName}'. The outbox entry references a type " +
                    $"that the Saga generator did not emit a dispatcher for. Ensure the type is " +
                    $"referenced from a [Step] method's return type in this compilation.");
        }
    }
}
```

Public visibility (consumed from `Saga.Outbox`'s poller, which lives in a separate assembly).

### Outbox bridge package

`ZeroAlloc.Saga.Outbox` ships:

```csharp
internal sealed class OutboxSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly IOutboxStore _store;
    private readonly IServiceProvider _services;

    public OutboxSagaCommandDispatcher(IOutboxStore store, IServiceProvider services)
    { _store = store; _services = services; }

    public async ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        var serializer = _services.GetRequiredService<ISerializer<TCommand>>();
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(buffer, cmd);
        await _store.EnqueueAsync(
            typeName: typeof(TCommand).FullName!,
            payload: buffer.WrittenMemory,
            transaction: null,
            ct: ct).ConfigureAwait(false);
    }
}
```

Plus an `IHostedService` poller that reads pending entries via `IOutboxStore.FetchPendingAsync()`,
calls `SagaCommandRegistry.DispatchAsync(...)` per entry, and marks results via the existing
`IOutboxStore` API (`MarkSucceededAsync`, `MarkFailedAsync`, `DeadLetterAsync`).

Plus a registration extension:

```csharp
public static class SagaOutboxRegistrationExtensions
{
    public static ISagaRegistrationBuilder<TSaga, TKey> WithOutbox<TSaga, TKey>(
        this ISagaRegistrationBuilder<TSaga, TKey> builder)
        where TSaga : class, new()
        where TKey : notnull, IEquatable<TKey>
    {
        builder.Services.Replace(ServiceDescriptor.Scoped<ISagaCommandDispatcher, OutboxSagaCommandDispatcher>());
        builder.Services.AddHostedService<OutboxSagaCommandPoller>();
        return builder;
    }
}
```

(The exact builder type name follows whatever Saga 1.1 already exposes for `AddSaga<>()`. Confirm
during implementation.)

### Transaction model

Both `EfCoreSagaStore<TSaga, TKey>` and `OutboxSagaCommandDispatcher`'s underlying `IOutboxStore`
(when implemented by `Outbox.EfCore`) inject the same scoped `DbContext`. The dispatcher's
`EnqueueAsync` adds a tracked outbox entity to the DbContext but does NOT call
`SaveChangesAsync`. The next `_store.SaveAsync(...)` call's `SaveChangesAsync` persists both
entities atomically.

If OCC conflicts on save, both rows roll back. Retry re-runs the saga handler from the top, which
re-tracks both entities. **No more duplicate dispatch.**

This atomicity contract relies on shared scoped `DbContext` — it works today with `Outbox.EfCore`
+ `Saga.EfCore`. Other backend pairings (e.g. `Outbox.InMemory` + `Saga.EfCore`) silently break
the contract: docs explicitly require both stores to share a transactional substrate.

## Auto-applied `[ZeroAllocSerializable]`

`Saga.Outbox` requires step-command types to have a `ZeroAlloc.Serialisation.ISerializer<T>`.
The Saga 1.2 generator auto-applies the marker attribute via partial extension when:

1. `ZeroAlloc.Serialisation.ZeroAllocSerializableAttribute` exists in the compilation references
   (detected via `Compilation.GetTypeByMetadataName`). When absent, the generator emits nothing
   serialization-related — core Saga has no hard dependency on Serialisation.
2. The step-method return-type is declared in the current compilation (cross-assembly types
   can't be partial-extended; ZASAGA017 fires).
3. The type is `partial` (ZASAGA016 fires + code-fix when not).
4. The user hasn't already applied `[ZeroAllocSerializable]` (the user's choice always wins).

Generator emit (one file per command type, e.g. `CreateOrder.SagaSerializable.g.cs`):

```csharp
[ZeroAlloc.Serialisation.ZeroAllocSerializable(SerializationFormat.Json)]
partial record struct CreateOrder;
```

C# spec: attributes on partial-type declarations merge across all parts. The
`ZeroAlloc.Serialisation.Generator` picks up the attribute via standard discovery and emits
`ISerializer<CreateOrder>` per its existing logic. Saga generator and Serialisation generator
don't need to coordinate — they both look at the same compilation.

**Format choice:** default `SerializationFormat.Json`. The user overrides per-command by
applying their own `[ZeroAllocSerializable(SerializationFormat.MessagePack)]` (or MemoryPack).
The Saga generator detects the explicit attribute and skips its default emit.

## New diagnostics (Saga 1.2)

| ID | Severity | Cause | Code-fix |
|---|---|---|---|
| **ZASAGA016** | Warning | A `[Step]` method's return-type is not `partial`, blocking auto-`[ZeroAllocSerializable]` emit | "Add `partial` modifier" |
| **ZASAGA017** | Info | A `[Step]` method's return-type lives in a referenced assembly; Saga generator can't extend it; user must apply `[ZeroAllocSerializable]` themselves | (none — user-side action) |

ZASAGA001–015 are taken (existing). ZASAGA014/015 shipped with Saga 1.1.

## Data flow (end-to-end, with Outbox enabled)

```
1. Event arrives — generated handler is invoked.
2. handler.Handle(event):
   2a. Acquire saga lock.
   2b. Load saga state.
   2c. Compute cmd via saga.MyStepMethod(event).
   2d. await _dispatcher.DispatchAsync(cmd, ct):
       — OutboxSagaCommandDispatcher resolves ISerializer<TCommand>
       — Serializes cmd to bytes
       — IOutboxStore.EnqueueAsync(typeName, bytes, null, ct):
         tracks an outbox entity in the DbContext (no SaveChanges yet)
   2e. Advance FSM state on the saga.
   2f. await _store.SaveAsync(key, saga, ct):
       — DbContext.SaveChangesAsync commits saga row + outbox row atomically
       — On OCC conflict: both roll back, retry loop re-runs from 2b
3. Background: OutboxSagaCommandPoller (IHostedService):
   3a. IOutboxStore.FetchPendingAsync(batchSize)
   3b. For each entry:
       — SagaCommandRegistry.DispatchAsync(typeName, bytes, services, mediator, ct)
       — Mark succeeded / failed / dead-letter via IOutboxStore
```

## Testing

- **Saga 1.2 generator tests** — snapshot tests for `_dispatcher.DispatchAsync` emit (replaces
  current `_mediator.Send` snapshot); snapshot tests for `SagaCommandRegistry` shape; snapshot
  tests for the partial-attribute extension when `ZeroAlloc.Serialisation` is referenced;
  ZASAGA016/017 diagnostic tests.
- **Default-dispatcher integration tests** — end-to-end saga round-trip using
  `MediatorSagaCommandDispatcher`, asserting existing 1.1 behavior unchanged.
- **Outbox-dispatcher integration tests** — end-to-end saga round-trip using
  `OutboxSagaCommandDispatcher`:
  - Saga step dispatches; outbox row is tracked but not yet visible in the table.
  - `_store.SaveAsync` commits both rows.
  - Poller drains, dispatches via Mediator.
  - Total flow: command runs exactly once.
- **OCC-conflict regression test** — the load-bearing test: provoke an OCC conflict on save
  AFTER dispatch, assert that the outbox row was NOT visible to the poller (both rolled back),
  and that on retry the command was dispatched exactly once. This locks in the fix for the
  Saga 1.1 caveat.
- **Cross-assembly command test** — a step-method return-type in a referenced assembly fires
  ZASAGA017 and does NOT auto-emit the partial extension.

## Migration notes (Saga 1.1 → 1.2)

For Saga 1.1 users who don't want outbox: zero changes. Generator output is binary-equivalent.
`AddSaga<MySaga>()` continues to register `MediatorSagaCommandDispatcher` as the default.

For Saga 1.1 users who want outbox:

1. Update `ZeroAlloc.Saga` reference to 1.2.
2. Add `ZeroAlloc.Saga.Outbox` reference.
3. Add `ZeroAlloc.Outbox.EfCore` reference (or another outbox backend that shares the saga's
   `DbContext`).
4. Add `ZeroAlloc.Serialisation` + `ZeroAlloc.Serialisation.SystemTextJson` (or another format
   adapter) references.
5. Mark step-command types `partial` (compiler will guide via ZASAGA016 if missed).
6. Wire DI:
   ```csharp
   services
       .AddSaga<MyOrderSaga, OrderId>()
       .UseEfCore<MyDbContext>()
       .WithOutbox();           // ← new
   services
       .AddOutbox()
       .UseEfCore<MyDbContext>();
   ```
7. Result: command dispatch is now atomic with saga state save. The "duplicate dispatch on OCC
   retry" caveat documented in Saga 1.1 README is fixed.

## Non-goals (deferred)

- **Resilience bridge.** `Saga.Resilience` and `[Step(Retry=N, CircuitBreaker=...)]` are Phase 3b
  (separate iteration). Phase 3a's dispatcher indirection is the integration point Phase 3b will
  use.
- **`ISagaUnitOfWork` abstraction.** No interface over `DbContext` this iteration. Defer until
  Saga.Redis (Phase 7) reveals what the abstraction needs to look like.
- **Cross-assembly commands.** ZASAGA017 documents the limitation; the user applies the attribute
  manually on their own declaration.
- **STJ-direct fallback.** `ZeroAlloc.Serialisation` is the single serialization path. AOT
  consumers get AOT-clean serialization automatically via Serialisation's source-gen.
- **Auto-installation of an outbox backend.** User installs an `Outbox.<backend>` package
  separately and wires it.
- **Outbox payload schema migration.** Existing pending outbox entries persist as-written.
  Schema changes to commands are the user's responsibility — same posture as the existing
  ZASAGA015 idempotency contract.
- **Multi-command atomic dispatch from one step.** A step returns ONE command. Coarse-grained
  commands fanned out by their handler are the workaround.
- **Outbox poller scaling / partitioning.** Phase 3a uses the existing `ZeroAlloc.Outbox`
  poller. Multi-worker / per-saga-key partitioning is a future-Outbox-core concern.

## Open implementation questions

- Exact name of the `WithOutbox()` builder return type (depends on Saga 1.1's
  `AddSaga<>()` chain — to be confirmed during implementation).
- `OutboxSagaCommandPoller` lifetime — singleton `IHostedService` is the obvious choice but it
  needs to create a scope per batch to resolve scoped `ISerializer<T>`s. Pattern: capture
  `IServiceScopeFactory`, create a scope per batch.
- Whether `SagaCommandRegistry.DispatchAsync` should run each entry in its own scope (likely yes,
  for clean DI lifetime semantics around the dispatched handler).
