# Saga Phase 3a — Outbox Bridge Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Eliminate the documented "command may dispatch twice on OCC retry" caveat by routing saga step commands through a transactional outbox in the same DB transaction as the saga state save.

**Architecture:** Add an `ISagaCommandDispatcher` indirection in Saga 1.2's generator output. Default impl forwards to `IMediator.Send` (existing behavior unchanged). New `ZeroAlloc.Saga.Outbox` package ships an alternative impl that writes to `IOutboxStore` and an `IHostedService` poller that drains the outbox via a generator-emitted `SagaCommandRegistry` switch table. Per-command serialization uses `ZeroAlloc.Serialisation` with `[ZeroAllocSerializable]` auto-applied via partial-class extension when the package is referenced.

**Tech Stack:** .NET 10, Roslyn IIncrementalGenerator, xUnit + Verify (snapshot tests), `ZeroAlloc.Mediator`, `ZeroAlloc.Outbox`, `ZeroAlloc.Serialisation` 2.1.0+, EF Core (for transactional pairing).

**Design doc:** `docs/plans/2026-05-04-saga-phase-3a-outbox-design.md`

---

## Conventions

- All work happens on branch `feat/v1.2-outbox-bridge-design` (already created and carries the design doc commit).
- Each task ends in a commit. Lowercase Conventional Commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`, `refactor:`).
- Build verification: `dotnet build ZeroAlloc.Saga.slnx -c Release --nologo` from repo root.
- Test verification: `dotnet test tests/<project>/<project>.csproj -c Release --nologo`.
- New public API additions in `ZeroAlloc.Saga` go in `src/ZeroAlloc.Saga/PublicAPI.Unshipped.txt` (alphabetical, `RS0016` will fail the build if missed).
- Saga generator uses snapshot tests (Verify) — when generator output changes, run the failing test once to refresh `.received.cs`, inspect the diff, then accept by renaming `.received.cs` → `.verified.cs`.
- Repo root: `c:/Projects/Prive/ZeroAlloc/ZeroAlloc.Saga`.

---

## Task 1: Add `ISagaCommandDispatcher` interface

**Files:**
- Create: `src/ZeroAlloc.Saga/ISagaCommandDispatcher.cs`
- Modify: `src/ZeroAlloc.Saga/PublicAPI.Unshipped.txt`
- Test: `tests/ZeroAlloc.Saga.Tests/ISagaCommandDispatcherTests.cs`

**Step 1: Write the failing test**

```csharp
// tests/ZeroAlloc.Saga.Tests/ISagaCommandDispatcherTests.cs
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga.Tests;

public class ISagaCommandDispatcherTests
{
    public readonly record struct TestCommand(int X) : IRequest<Unit>;

    [Fact]
    public async Task Default_ImplementationContract_ForwardsToCallback()
    {
        var dispatcher = new RecordingDispatcher();
        await dispatcher.DispatchAsync(new TestCommand(42), CancellationToken.None);
        Assert.Equal(42, dispatcher.LastValue);
    }

    private sealed class RecordingDispatcher : ISagaCommandDispatcher
    {
        public int LastValue { get; private set; }
        public ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct) where TCommand : IRequest<Unit>
        {
            if (cmd is TestCommand tc) LastValue = tc.X;
            return default;
        }
    }
}
```

**Step 2: Run — expect build error (`ISagaCommandDispatcher` not defined)**

```bash
cd c:/Projects/Prive/ZeroAlloc/ZeroAlloc.Saga
dotnet test tests/ZeroAlloc.Saga.Tests/ZeroAlloc.Saga.Tests.csproj --filter "FullyQualifiedName~ISagaCommandDispatcherTests" --nologo
```

**Step 3: Implement the interface**

```csharp
// src/ZeroAlloc.Saga/ISagaCommandDispatcher.cs
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga;

/// <summary>
/// Indirection layer between Saga's generated handlers and the actual command-dispatch
/// mechanism. Default implementation (<see cref="MediatorSagaCommandDispatcher"/>) forwards
/// to <see cref="IMediator.Send"/>; the <c>ZeroAlloc.Saga.Outbox</c> package supplies an
/// alternative implementation that writes to a transactional outbox so the dispatch commits
/// atomically with the saga state save.
/// </summary>
public interface ISagaCommandDispatcher
{
    /// <summary>Dispatch a saga step command. Implementations must be thread-safe.</summary>
    ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>;
}
```

**Step 4: Update PublicAPI.Unshipped.txt**

Add (alphabetical):

```
ZeroAlloc.Saga.ISagaCommandDispatcher
ZeroAlloc.Saga.ISagaCommandDispatcher.DispatchAsync<TCommand>(TCommand cmd, System.Threading.CancellationToken ct) -> System.Threading.Tasks.ValueTask
```

**Step 5: Run — expect pass**

```bash
dotnet test tests/ZeroAlloc.Saga.Tests/ZeroAlloc.Saga.Tests.csproj --filter "FullyQualifiedName~ISagaCommandDispatcherTests" --nologo
```

**Step 6: Commit**

```bash
git add src/ZeroAlloc.Saga/ISagaCommandDispatcher.cs \
        src/ZeroAlloc.Saga/PublicAPI.Unshipped.txt \
        tests/ZeroAlloc.Saga.Tests/ISagaCommandDispatcherTests.cs
git commit -m "feat: add isagacommanddispatcher indirection"
```

---

## Task 2: Add `MediatorSagaCommandDispatcher` default impl

**Files:**
- Create: `src/ZeroAlloc.Saga/MediatorSagaCommandDispatcher.cs`
- Modify: `src/ZeroAlloc.Saga/PublicAPI.Unshipped.txt`
- Test: `tests/ZeroAlloc.Saga.Tests/MediatorSagaCommandDispatcherTests.cs`

**Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga.Tests;

public class MediatorSagaCommandDispatcherTests
{
    public readonly record struct PingCmd(string Msg) : IRequest<Unit>;

    public class PingHandler : IRequestHandler<PingCmd, Unit>
    {
        public static string? LastMessage;
        public ValueTask<Unit> Handle(PingCmd request, CancellationToken ct)
        { LastMessage = request.Msg; return ValueTask.FromResult(Unit.Value); }
    }

    [Fact]
    public async Task DispatchAsync_ForwardsToIMediator()
    {
        PingHandler.LastMessage = null;
        var services = new ServiceCollection();
        services.AddMediator().RegisterHandlersFromAssembly(typeof(MediatorSagaCommandDispatcherTests).Assembly);
        using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        var dispatcher = new MediatorSagaCommandDispatcher(mediator);
        await dispatcher.DispatchAsync(new PingCmd("hello"), CancellationToken.None);

        Assert.Equal("hello", PingHandler.LastMessage);
    }
}
```

**Step 2: Run — expect build error**

```bash
dotnet test tests/ZeroAlloc.Saga.Tests/ZeroAlloc.Saga.Tests.csproj --filter "FullyQualifiedName~MediatorSagaCommandDispatcherTests" --nologo
```

**Step 3: Implement**

```csharp
// src/ZeroAlloc.Saga/MediatorSagaCommandDispatcher.cs
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga;

/// <summary>
/// Default <see cref="ISagaCommandDispatcher"/>: forwards to <see cref="IMediator.Send"/>.
/// Registered automatically by <c>services.AddSaga&lt;TSaga&gt;()</c>. Replace with
/// <c>WithOutbox()</c> from <c>ZeroAlloc.Saga.Outbox</c> for transactional dispatch.
/// </summary>
public sealed class MediatorSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly IMediator _mediator;
    public MediatorSagaCommandDispatcher(IMediator mediator) => _mediator = mediator;

    public async ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        await _mediator.Send(cmd, ct).ConfigureAwait(false);
    }
}
```

**Step 4: Update PublicAPI.Unshipped.txt**

```
ZeroAlloc.Saga.MediatorSagaCommandDispatcher
ZeroAlloc.Saga.MediatorSagaCommandDispatcher.MediatorSagaCommandDispatcher(ZeroAlloc.Mediator.IMediator! mediator) -> void
ZeroAlloc.Saga.MediatorSagaCommandDispatcher.DispatchAsync<TCommand>(TCommand cmd, System.Threading.CancellationToken ct) -> System.Threading.Tasks.ValueTask
```

**Step 5: Run — expect pass**

**Step 6: Commit**

```bash
git add src/ZeroAlloc.Saga/MediatorSagaCommandDispatcher.cs \
        src/ZeroAlloc.Saga/PublicAPI.Unshipped.txt \
        tests/ZeroAlloc.Saga.Tests/MediatorSagaCommandDispatcherTests.cs
git commit -m "feat: add mediatorsagacommanddispatcher default impl"
```

---

## Task 3: Register `MediatorSagaCommandDispatcher` from `AddSaga<>()`

**Files:**
- Modify: `src/ZeroAlloc.Saga/<wherever AddSaga lives>` (find via grep)
- Test: `tests/ZeroAlloc.Saga.Tests/AddSagaRegistrationTests.cs` (new or extend existing)

**Step 1: Find current `AddSaga` registration**

```bash
grep -rn "AddSaga\|IServiceCollection" src/ZeroAlloc.Saga/*.cs | head
```

The Saga 1.1 builder lives somewhere in core. The generator emits a per-saga builder extension (see `BuilderExtensionsEmitter.cs`). The dispatcher registration needs to happen at the entry-point so it's shared across all sagas in the app.

**Step 2: Write the failing test**

```csharp
[Fact]
public void AddSaga_RegistersMediatorSagaCommandDispatcher_AsScopedDefault()
{
    var services = new ServiceCollection();
    services.AddMediator(); // dispatcher requires IMediator
    services.AddSaga<TestSaga, TestKey>(); // existing API
    var d = services.SingleOrDefault(x => x.ServiceType == typeof(ISagaCommandDispatcher));
    Assert.NotNull(d);
    Assert.Equal(typeof(MediatorSagaCommandDispatcher), d!.ImplementationType);
}
```

(Replace `TestSaga` / `TestKey` with whatever minimal types the existing `AddSaga` wires up; reuse a test fixture from `tests/ZeroAlloc.Saga.Tests/Fixtures` if available.)

**Step 3: Add registration**

In whichever method registers the saga's services (likely in a `BuilderExtensionsEmitter` — confirm by reading that file), add at the entry point:

```csharp
services.TryAddScoped<ISagaCommandDispatcher, MediatorSagaCommandDispatcher>();
```

(Use `TryAdd` so a `WithOutbox()` call later can `Replace`.)

**Step 4: Run — expect pass**

```bash
dotnet test tests/ZeroAlloc.Saga.Tests/ZeroAlloc.Saga.Tests.csproj --filter "FullyQualifiedName~AddSagaRegistrationTests" --nologo
```

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Saga/ tests/ZeroAlloc.Saga.Tests/
git commit -m "feat: register mediatorsagacommanddispatcher as scoped default"
```

---

## Task 4: Update generator to emit `_dispatcher.DispatchAsync`

**Files:**
- Modify: `src/ZeroAlloc.Saga.Generator/HandlerEmitter.cs` (replace `_mediator.Send` emissions)
- Modify: `tests/ZeroAlloc.Saga.Generator.Tests/Snapshots/*.verified.cs` (refresh)

**Step 1: Read existing emitter**

```bash
grep -n "_mediator\|IMediator" src/ZeroAlloc.Saga.Generator/HandlerEmitter.cs
```

Three call sites to change:
1. Forward step dispatch: `await _mediator.Send(cmd, ct).ConfigureAwait(false);`
2. Compensation dispatch: `await _mediator.Send(saga.MyCompensateMethod(), ct).ConfigureAwait(false);`
3. Constructor parameter and field declaration.

**Step 2: Update emitter**

Replace `_mediator` field/parameter with `_dispatcher`. Change the type from `IMediator` to `ISagaCommandDispatcher`. Change `.Send(cmd, ct)` calls to `.DispatchAsync(cmd, ct)`.

Concrete diff (in `HandlerEmitter.cs`):

```csharp
// Before:
sb.AppendLine("    private readonly IMediator _mediator;");
// After:
sb.AppendLine("    private readonly ISagaCommandDispatcher _dispatcher;");
```

```csharp
// Before:
sb.AppendLine("        IMediator mediator,");
// After:
sb.AppendLine("        ISagaCommandDispatcher dispatcher,");
```

```csharp
// Before:
sb.AppendLine("        _mediator = mediator;");
// After:
sb.AppendLine("        _dispatcher = dispatcher;");
```

```csharp
// Before:
sb.AppendLine("                await _mediator.Send(cmd, ct).ConfigureAwait(false);");
// After:
sb.AppendLine("                await _dispatcher.DispatchAsync(cmd, ct).ConfigureAwait(false);");
```

```csharp
// Before (compensation):
sb.Append("                        await _mediator.Send(saga.").Append(s.CompensateMethodName).AppendLine("(), ct).ConfigureAwait(false);");
// After:
sb.Append("                        await _dispatcher.DispatchAsync(saga.").Append(s.CompensateMethodName).AppendLine("(), ct).ConfigureAwait(false);");
```

Also update the emitted `using ZeroAlloc.Mediator;` line — keep it (the `IRequest` constraint is still there) and add `using ZeroAlloc.Saga;` if not already imported.

**Step 3: Run snapshot tests — expect failures**

```bash
dotnet test tests/ZeroAlloc.Saga.Generator.Tests/ZeroAlloc.Saga.Generator.Tests.csproj --nologo
```

Multiple `*_Handler.g.verified.cs` snapshots will mismatch. Each test produces a `.received.cs` showing the new output.

**Step 4: Refresh snapshots**

For each mismatched snapshot, inspect the diff (it should ONLY show `_mediator` → `_dispatcher` and `IMediator` → `ISagaCommandDispatcher`). If correct, accept by renaming:

```bash
cd tests/ZeroAlloc.Saga.Generator.Tests/Snapshots
for f in *.received.cs; do
  echo "Refreshing: $f"
  mv -f "$f" "${f%.received.cs}.verified.cs"
done
cd ../../..
```

**Step 5: Re-run snapshot tests — expect pass**

```bash
dotnet test tests/ZeroAlloc.Saga.Generator.Tests/ZeroAlloc.Saga.Generator.Tests.csproj --nologo
```

**Step 6: Run all Saga test projects to check nothing else breaks**

```bash
dotnet test tests/ZeroAlloc.Saga.Tests/ZeroAlloc.Saga.Tests.csproj --nologo
dotnet test tests/ZeroAlloc.Saga.Diagnostics.Tests/ZeroAlloc.Saga.Diagnostics.Tests.csproj --nologo
dotnet test tests/ZeroAlloc.Saga.EfCore.Tests/ZeroAlloc.Saga.EfCore.Tests.csproj --nologo
```

All must pass — the existing E2E tests should still work because `MediatorSagaCommandDispatcher` forwards to `IMediator.Send` exactly as before.

**Step 7: Commit**

```bash
git add src/ZeroAlloc.Saga.Generator/HandlerEmitter.cs \
        tests/ZeroAlloc.Saga.Generator.Tests/Snapshots/
git commit -m "feat(generator): emit _dispatcher.DispatchAsync instead of _mediator.Send"
```

---

## Task 5: Generator emits public `SagaCommandRegistry`

**Files:**
- Create: `src/ZeroAlloc.Saga.Generator/SagaCommandRegistryEmitter.cs`
- Modify: `src/ZeroAlloc.Saga.Generator/SagaGenerator.cs` (call the new emitter)
- Test: `tests/ZeroAlloc.Saga.Generator.Tests/SagaCommandRegistrySnapshotTests.cs`

**Step 1: Write the failing snapshot test**

```csharp
// tests/ZeroAlloc.Saga.Generator.Tests/SagaCommandRegistrySnapshotTests.cs
using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Generator.Tests;

public class SagaCommandRegistrySnapshotTests
{
    [Fact]
    public Task EmitsRegistry_ForSingleSaga()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;
            using ZeroAlloc.Serialisation;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;

            public partial readonly record struct ReserveStockCommand(OrderId OrderId) : IRequest<Unit>;
            public partial readonly record struct ChargeCardCommand(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class TwoStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 2)] public ChargeCardCommand Charge(OrderPlaced e) => new(e.OrderId);
            }
            """;

        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }
}
```

This test references `ZeroAlloc.Serialisation` to trigger registry emission (registry is conditional on Serialisation being referenced).

**Step 2: Run — expect failure (no `SagaCommandRegistry.g.verified.cs` snapshot)**

**Step 3: Implement the emitter**

`src/ZeroAlloc.Saga.Generator/SagaCommandRegistryEmitter.cs`:

```csharp
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Emits a per-compilation public static class <c>ZeroAlloc.Saga.Generated.SagaCommandRegistry</c>
/// with a switch on type name → deserialize via ISerializer&lt;T&gt; → IMediator.Send.
/// Used by ZeroAlloc.Saga.Outbox's poller to dispatch enqueued saga commands.
/// </summary>
internal static class SagaCommandRegistryEmitter
{
    public static void Emit(SourceProductionContext spc, IEnumerable<SagaModel> sagas, bool serialisationReferenced)
    {
        if (!serialisationReferenced) return; // skip emission entirely

        var commandTypes = sagas
            .SelectMany(s => s.Steps.Select(st => st.CommandTypeFqn))
            .Concat(sagas.SelectMany(s => s.Steps
                .Where(st => st.CompensateCommandTypeFqn is not null)
                .Select(st => st.CompensateCommandTypeFqn!)))
            .Distinct(System.StringComparer.Ordinal)
            .OrderBy(x => x, System.StringComparer.Ordinal)
            .ToList();

        if (commandTypes.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using ZeroAlloc.Mediator;");
        sb.AppendLine("using ZeroAlloc.Serialisation;");
        sb.AppendLine();
        sb.AppendLine("namespace ZeroAlloc.Saga.Generated;");
        sb.AppendLine();
        sb.AppendLine("public static class SagaCommandRegistry");
        sb.AppendLine("{");
        sb.AppendLine("    public static async ValueTask DispatchAsync(");
        sb.AppendLine("        string typeName,");
        sb.AppendLine("        ReadOnlyMemory<byte> bytes,");
        sb.AppendLine("        IServiceProvider services,");
        sb.AppendLine("        IMediator mediator,");
        sb.AppendLine("        CancellationToken ct)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (typeName)");
        sb.AppendLine("        {");
        foreach (var fqn in commandTypes)
        {
            // fqn is e.g. "global::Sample.ReserveStockCommand"; strip leading "global::"
            var bare = fqn.StartsWith("global::") ? fqn.Substring("global::".Length) : fqn;
            sb.Append("            case \"").Append(bare).AppendLine("\":");
            sb.AppendLine("            {");
            sb.Append("                var serializer = services.GetRequiredService<ISerializer<").Append(fqn).AppendLine(">>();");
            sb.AppendLine("                var cmd = serializer.Deserialize(bytes.Span);");
            sb.AppendLine("                if (cmd is null)");
            sb.Append("                    throw new InvalidOperationException(\"ISerializer.Deserialize returned null for ").Append(bare).AppendLine(".\");");
            sb.AppendLine("                await mediator.Send(cmd, ct).ConfigureAwait(false);");
            sb.AppendLine("                return;");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            default:");
        sb.AppendLine("                throw new InvalidOperationException(");
        sb.AppendLine("                    $\"Unknown saga command type '{typeName}'. The outbox entry references a type that the Saga generator did not emit a dispatcher for.\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("SagaCommandRegistry.g.cs", sb.ToString());
    }
}
```

**Step 4: Wire into `SagaGenerator.Initialize`**

In `src/ZeroAlloc.Saga.Generator/SagaGenerator.cs`, after the existing `RegisterSourceOutput` for handler emission, add:

```csharp
var serialisationReferenced = context.CompilationProvider
    .Select((compilation, _) =>
        compilation.GetTypeByMetadataName("ZeroAlloc.Serialisation.ZeroAllocSerializableAttribute") is not null);

context.RegisterSourceOutput(
    sagas.Collect().Combine(serialisationReferenced),
    static (spc, tuple) =>
    {
        var (sagaModels, hasSerialisation) = tuple;
        SagaCommandRegistryEmitter.Emit(spc, sagaModels, hasSerialisation);
    });
```

(Adapt to whatever provider name the existing `Initialize` uses. Read it first.)

**Step 5: Refresh / accept snapshot**

Run the test, inspect the new `SagaCommandRegistrySnapshotTests.EmitsRegistry_ForSingleSaga#SagaCommandRegistry.g.received.cs`. If it looks right, accept:

```bash
mv tests/ZeroAlloc.Saga.Generator.Tests/Snapshots/SagaCommandRegistrySnapshotTests.EmitsRegistry_ForSingleSaga#SagaCommandRegistry.g.received.cs \
   tests/ZeroAlloc.Saga.Generator.Tests/Snapshots/SagaCommandRegistrySnapshotTests.EmitsRegistry_ForSingleSaga#SagaCommandRegistry.g.verified.cs
```

**Step 6: Re-run — expect pass**

**Step 7: Commit**

```bash
git add src/ZeroAlloc.Saga.Generator/SagaCommandRegistryEmitter.cs \
        src/ZeroAlloc.Saga.Generator/SagaGenerator.cs \
        tests/ZeroAlloc.Saga.Generator.Tests/SagaCommandRegistrySnapshotTests.cs \
        tests/ZeroAlloc.Saga.Generator.Tests/Snapshots/SagaCommandRegistrySnapshotTests.*.verified.cs
git commit -m "feat(generator): emit sagacommandregistry switch-table when serialisation is referenced"
```

---

## Task 6: ZASAGA016 — `[Step]` command type must be `partial`

**Files:**
- Modify: `src/ZeroAlloc.Saga.Generator/Diagnostics/SagaDiagnostics.cs` (or wherever existing diagnostics live)
- Modify: `src/ZeroAlloc.Saga.Generator/SagaGenerator.cs` (fire diagnostic during model build)
- Modify: `src/ZeroAlloc.Saga.Generator/AnalyzerReleases.Unshipped.md`
- Test: `tests/ZeroAlloc.Saga.Diagnostics.Tests/DiagnosticTests.cs` (add new test)

**Step 1: Find existing diagnostic infrastructure**

```bash
ls src/ZeroAlloc.Saga.Generator/Diagnostics/
cat src/ZeroAlloc.Saga.Generator/AnalyzerReleases.Shipped.md | tail -10
cat src/ZeroAlloc.Saga.Generator/AnalyzerReleases.Unshipped.md
```

**Step 2: Write the failing diagnostic test**

```csharp
[Fact]
public async Task ZASAGA016_FiresWhen_StepCommandType_IsNotPartial_AndSerialisationReferenced()
{
    var src = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;
        using ZeroAlloc.Serialisation;
        namespace Sample;

        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public sealed record OrderPlaced(OrderId OrderId) : INotification;
        public readonly record struct ReserveStockCommand(OrderId OrderId) : IRequest<Unit>;

        [Saga]
        public partial class TwoStepSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
        }
        """;
    var diagnostics = await GeneratorVerifier.RunAsync(src);
    Assert.Contains(diagnostics, d => d.Id == "ZASAGA016");
}
```

(Use whatever helper `DiagnosticTests.cs` already uses for running the generator and asserting on diagnostics — match the existing pattern in that file.)

**Step 3: Run — expect failure (no ZASAGA016 fires)**

**Step 4: Add the diagnostic descriptor**

In `Diagnostics/SagaDiagnostics.cs`:

```csharp
public static readonly DiagnosticDescriptor StepCommandTypeNotPartial = new(
    id: "ZASAGA016",
    title: "Step command type must be partial when ZeroAlloc.Serialisation is referenced",
    messageFormat: "Step command type '{0}' must be 'partial' so the Saga generator can apply [ZeroAllocSerializable] via partial-class extension. Add the 'partial' modifier.",
    category: "ZeroAlloc.Saga.Authoring",
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true);
```

**Step 5: Fire it during model build**

In `SagaGenerator` (or wherever step-command types are inspected), when `ZeroAllocSerializableAttribute` is in the compilation references AND the step command type is in source AND the type is not `partial`, report:

```csharp
spc.ReportDiagnostic(Diagnostic.Create(
    SagaDiagnostics.StepCommandTypeNotPartial,
    location: stepCommandTypeSyntax.Identifier.GetLocation(),
    stepCommandType.ToDisplayString()));
```

**Step 6: Update AnalyzerReleases.Unshipped.md**

Add:

```
ZASAGA016 | ZeroAlloc.Saga.Authoring | Warning | Step command type must be partial when ZeroAlloc.Serialisation is referenced
```

**Step 7: Run — expect pass**

**Step 8: Commit**

```bash
git add src/ZeroAlloc.Saga.Generator/ \
        tests/ZeroAlloc.Saga.Diagnostics.Tests/DiagnosticTests.cs
git commit -m "feat(generator): zasaga016 fires when step command type is not partial"
```

---

## Task 7: ZASAGA017 — cross-assembly step command type

**Files:**
- Modify: `src/ZeroAlloc.Saga.Generator/Diagnostics/SagaDiagnostics.cs`
- Modify: `src/ZeroAlloc.Saga.Generator/SagaGenerator.cs`
- Modify: `src/ZeroAlloc.Saga.Generator/AnalyzerReleases.Unshipped.md`
- Test: `tests/ZeroAlloc.Saga.Diagnostics.Tests/DiagnosticTests.cs`

**Step 1: Test for cross-assembly types**

`GeneratorVerifier`'s test fixture probably allows configuring referenced assemblies. Set up a test where the step command type is from a referenced assembly (not the test source), then assert ZASAGA017 fires. If `GeneratorVerifier` doesn't easily support multi-assembly compilations, simplify: use a synthetic reference that proves the same path (e.g. a type from `System.Collections.Generic`). The detection should be: `commandTypeSymbol.ContainingAssembly != currentCompilation.Assembly`.

```csharp
[Fact]
public async Task ZASAGA017_FiresWhen_StepCommandType_IsCrossAssembly_AndSerialisationReferenced()
{
    var src = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;
        using ZeroAlloc.Serialisation;
        namespace Sample;

        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public sealed record OrderPlaced(OrderId OrderId) : INotification;

        [Saga]
        public partial class CrossAssemblySaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            // Use an external-assembly type: System.Tuple<int,int> (in mscorlib).
            // It implements neither IRequest<Unit> nor a saga return-shape, so this test
            // would normally fail compile. Replace with a real cross-assembly IRequest
            // type if the test infrastructure permits, or use a custom assembly fixture.
            [Step(Order = 1)] public ZeroAlloc.Saga.Tests.External.ExternalCommand Step1(OrderPlaced e) => new(e.OrderId.V);
        }
        """;
    var diagnostics = await GeneratorVerifier.RunAsync(src,
        additionalAssemblies: new[] { typeof(ZeroAlloc.Saga.Tests.External.ExternalCommand).Assembly });
    Assert.Contains(diagnostics, d => d.Id == "ZASAGA017");
}
```

If the test infrastructure can't easily reference an external assembly's types, **defer this test to integration-level** in a follow-up commit and just add the diagnostic descriptor + emit logic now. Document the deferral in the test file with a `// TODO: cross-assembly fixture` comment.

**Step 2: Add the diagnostic descriptor**

```csharp
public static readonly DiagnosticDescriptor StepCommandTypeCrossAssembly = new(
    id: "ZASAGA017",
    title: "Step command type is in a referenced assembly",
    messageFormat: "Step command type '{0}' is declared in a referenced assembly. The Saga generator cannot apply [ZeroAllocSerializable] via partial-class extension on foreign types. Apply [ZeroAllocSerializable] manually on the type's declaration.",
    category: "ZeroAlloc.Saga.Authoring",
    defaultSeverity: DiagnosticSeverity.Info,
    isEnabledByDefault: true);
```

**Step 3: Fire diagnostic in generator**

```csharp
if (serialisationReferenced
    && stepCommandTypeSymbol is INamedTypeSymbol named
    && !SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, currentCompilation.Assembly)
    && /* user didn't explicitly apply [ZeroAllocSerializable] on the foreign type */ )
{
    spc.ReportDiagnostic(Diagnostic.Create(
        SagaDiagnostics.StepCommandTypeCrossAssembly,
        location: stepCommandTypeSyntax.Identifier.GetLocation(),
        named.ToDisplayString()));
}
```

**Step 4: Update `AnalyzerReleases.Unshipped.md`**

```
ZASAGA017 | ZeroAlloc.Saga.Authoring | Info | Step command type is in a referenced assembly
```

**Step 5: Run — expect pass**

**Step 6: Commit**

```bash
git add src/ZeroAlloc.Saga.Generator/ tests/ZeroAlloc.Saga.Diagnostics.Tests/
git commit -m "feat(generator): zasaga017 fires for cross-assembly step command types"
```

---

## Task 8: Code-fix for ZASAGA016 (add `partial` modifier)

**Files:**
- Create: `src/ZeroAlloc.Saga.Generator/CodeFixes/AddPartialModifierCodeFix.cs`
- Test: `tests/ZeroAlloc.Saga.Diagnostics.Tests/CodeFixTests.cs`

**Step 1: Look at existing code-fix in the repo for shape**

```bash
ls src/ZeroAlloc.Saga.Generator/CodeFixes/
```

(Should already have at least one — Saga 1.0 ships ZASAGA007/ZASAGA009 code-fixes. Mirror that.)

**Step 2: Write the failing test**

```csharp
[Fact]
public async Task ZASAGA016_CodeFix_AddsPartialModifier()
{
    var before = """
        public readonly record struct ReserveStockCommand(int X) : IRequest<Unit>;
        """;
    var after = """
        public readonly partial record struct ReserveStockCommand(int X) : IRequest<Unit>;
        """;
    await CodeFixVerifier.VerifyAsync<AddPartialModifierCodeFix>(before, after, diagnosticId: "ZASAGA016");
}
```

(Match whatever signature `CodeFixVerifier` already has in `tests/ZeroAlloc.Saga.Diagnostics.Tests/CodeFixVerifier.cs`.)

**Step 3: Implement the code-fix**

Standard Roslyn `CodeFixProvider` that finds the type's `TypeDeclarationSyntax` and adds the `partial` modifier token. Reuse imports from existing code-fixes.

**Step 4: Run — expect pass**

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Saga.Generator/CodeFixes/AddPartialModifierCodeFix.cs \
        tests/ZeroAlloc.Saga.Diagnostics.Tests/CodeFixTests.cs
git commit -m "feat(generator): code-fix for zasaga016 adds partial modifier"
```

---

## Task 9: Generator emits `[ZeroAllocSerializable]` partial extension

**Files:**
- Create: `src/ZeroAlloc.Saga.Generator/SerializableExtensionEmitter.cs`
- Modify: `src/ZeroAlloc.Saga.Generator/SagaGenerator.cs`
- Test: `tests/ZeroAlloc.Saga.Generator.Tests/SerializableExtensionSnapshotTests.cs`

**Step 1: Failing snapshot test**

```csharp
[Fact]
public Task EmitsZeroAllocSerializable_OnPartialStepCommand()
{
    var src = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;
        using ZeroAlloc.Serialisation;

        namespace Sample;

        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public sealed record OrderPlaced(OrderId OrderId) : INotification;
        public partial readonly record struct ReserveStockCommand(OrderId OrderId) : IRequest<Unit>;

        [Saga]
        public partial class SingleStepSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
        }
        """;
    return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
}

[Fact]
public Task SkipsEmission_WhenUserAlreadyAppliedZeroAllocSerializable()
{
    var src = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;
        using ZeroAlloc.Serialisation;

        namespace Sample;

        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public sealed record OrderPlaced(OrderId OrderId) : INotification;

        [ZeroAllocSerializable(SerializationFormat.MessagePack)]
        public partial readonly record struct ReserveStockCommand(OrderId OrderId) : IRequest<Unit>;

        [Saga]
        public partial class SingleStepSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
        }
        """;
    return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
}
```

**Step 2: Implement the emitter**

`src/ZeroAlloc.Saga.Generator/SerializableExtensionEmitter.cs`:

```csharp
internal static class SerializableExtensionEmitter
{
    public static void Emit(SourceProductionContext spc, SagaModel model, Compilation compilation)
    {
        var serialisableAttr = compilation.GetTypeByMetadataName("ZeroAlloc.Serialisation.ZeroAllocSerializableAttribute");
        if (serialisableAttr is null) return;

        foreach (var step in model.Steps)
        {
            EmitFor(spc, step.CommandTypeSymbol, serialisableAttr, compilation);
            if (step.CompensateCommandSymbol is not null)
                EmitFor(spc, step.CompensateCommandSymbol, serialisableAttr, compilation);
        }
    }

    private static void EmitFor(SourceProductionContext spc, INamedTypeSymbol type, INamedTypeSymbol serialisableAttr, Compilation compilation)
    {
        // Skip cross-assembly types (ZASAGA017 covers this case)
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) return;

        // Skip if user already applied [ZeroAllocSerializable] explicitly
        if (type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, serialisableAttr))) return;

        // Skip if not declared partial — ZASAGA016 covers this case
        var declaringSyntaxReferences = type.DeclaringSyntaxReferences;
        if (declaringSyntaxReferences.Length == 0) return;
        var firstDecl = declaringSyntaxReferences[0].GetSyntax() as TypeDeclarationSyntax;
        if (firstDecl is null || !firstDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))) return;

        // Emit partial declaration carrying [ZeroAllocSerializable(SerializationFormat.Json)]
        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
        var typeKindKeyword = type.IsRecord
            ? (type.IsValueType ? "record struct" : "record")
            : (type.IsValueType ? "struct" : "class");
        var fileNameSafe = type.Name; // simple in-namespace types

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using ZeroAlloc.Serialisation;");
        sb.AppendLine();
        if (ns is not null) { sb.Append("namespace ").Append(ns).AppendLine(";"); sb.AppendLine(); }
        sb.AppendLine("[ZeroAllocSerializable(SerializationFormat.Json)]");
        sb.Append("partial ").Append(typeKindKeyword).Append(' ').Append(type.Name).AppendLine(";");

        spc.AddSource($"{fileNameSafe}.SagaSerializable.g.cs", sb.ToString());
    }
}
```

**Step 3: Wire into `SagaGenerator.Initialize`**

After the existing handler-emit `RegisterSourceOutput`, add a parallel one that calls `SerializableExtensionEmitter.Emit(spc, model, compilation)` per saga model.

**Step 4: Refresh snapshots, accept**

**Step 5: Run — expect pass**

**Step 6: Commit**

```bash
git add src/ZeroAlloc.Saga.Generator/SerializableExtensionEmitter.cs \
        src/ZeroAlloc.Saga.Generator/SagaGenerator.cs \
        tests/ZeroAlloc.Saga.Generator.Tests/
git commit -m "feat(generator): auto-apply zeroallocserializable on partial step commands"
```

---

## Task 10: New package `ZeroAlloc.Saga.Outbox`

**Files:**
- Create: `src/ZeroAlloc.Saga.Outbox/ZeroAlloc.Saga.Outbox.csproj`
- Create: `src/ZeroAlloc.Saga.Outbox/PublicAPI.Shipped.txt` (empty `#nullable enable`)
- Create: `src/ZeroAlloc.Saga.Outbox/PublicAPI.Unshipped.txt`
- Modify: `ZeroAlloc.Saga.slnx` — add the new project

**Step 1: Create the csproj (mirror `ZeroAlloc.Saga.EfCore.csproj` shape)**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>ZeroAlloc.Saga.Outbox</RootNamespace>
    <PackageId>ZeroAlloc.Saga.Outbox</PackageId>
    <Description>Transactional outbox bridge for ZeroAlloc.Saga — atomic command dispatch with saga state save.</Description>
    <PackageTags>saga;outbox;mediator;cqrs;zeroalloc</PackageTags>
    <IsAotCompatible>true</IsAotCompatible>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ZeroAlloc.Saga" />
    <PackageReference Include="ZeroAlloc.Outbox" />
    <PackageReference Include="ZeroAlloc.Serialisation" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```

For dependency versions: check `Directory.Build.props` and `Directory.Packages.props` for the family's `<CentralPackageVersion>` declarations and add new ones if needed.

**Step 2: Empty PublicAPI files**

```
# PublicAPI.Shipped.txt
#nullable enable
```

```
# PublicAPI.Unshipped.txt
#nullable enable
```

**Step 3: Add to `ZeroAlloc.Saga.slnx`**

Look at the existing entries in `ZeroAlloc.Saga.slnx`. Add a `<Project Path="src/ZeroAlloc.Saga.Outbox/ZeroAlloc.Saga.Outbox.csproj" />` line in the same group as the other `src/` projects.

**Step 4: Build**

```bash
dotnet build src/ZeroAlloc.Saga.Outbox/ZeroAlloc.Saga.Outbox.csproj -c Release --nologo
```

Expected: succeeds with empty package (only the `#nullable enable` PublicAPI files).

**Step 5: Commit**

```bash
git add src/ZeroAlloc.Saga.Outbox/ ZeroAlloc.Saga.slnx
git commit -m "feat: add zeroalloc.saga.outbox package skeleton"
```

---

## Task 11: `OutboxSagaCommandDispatcher`

**Files:**
- Create: `src/ZeroAlloc.Saga.Outbox/OutboxSagaCommandDispatcher.cs`
- Modify: `src/ZeroAlloc.Saga.Outbox/PublicAPI.Unshipped.txt`
- Test: `tests/ZeroAlloc.Saga.Outbox.Tests/OutboxSagaCommandDispatcherTests.cs` (new test project)

**Step 1: Create the test project**

`tests/ZeroAlloc.Saga.Outbox.Tests/ZeroAlloc.Saga.Outbox.Tests.csproj` (mirror `ZeroAlloc.Saga.EfCore.Tests.csproj`):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);MA0048;MA0051;ZAM003;EPC12</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\ZeroAlloc.Saga.Outbox\ZeroAlloc.Saga.Outbox.csproj" />
  </ItemGroup>
</Project>
```

Add to `ZeroAlloc.Saga.slnx`.

**Step 2: Failing test**

```csharp
// tests/ZeroAlloc.Saga.Outbox.Tests/OutboxSagaCommandDispatcherTests.cs
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga.Outbox;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox.Tests;

public class OutboxSagaCommandDispatcherTests
{
    public partial record struct DispatchableCmd(int X) : IRequest<Unit>;

    private sealed class FakeSerializer : ISerializer<DispatchableCmd>
    {
        public void Serialize(IBufferWriter<byte> writer, DispatchableCmd value)
        {
            var span = writer.GetSpan(4);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value.X);
            writer.Advance(4);
        }
        public DispatchableCmd? Deserialize(System.ReadOnlySpan<byte> buffer)
            => new DispatchableCmd(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer));
    }

    private sealed class CapturingOutboxStore : IOutboxStore
    {
        public string? CapturedTypeName;
        public byte[]? CapturedPayload;
        public ValueTask EnqueueAsync(string typeName, System.ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
        {
            CapturedTypeName = typeName;
            CapturedPayload = payload.ToArray();
            return default;
        }
        // … other IOutboxStore members not exercised by this test, throw NotImplementedException
        public ValueTask<System.Collections.Generic.IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct) => throw new System.NotImplementedException();
        public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct) => throw new System.NotImplementedException();
        public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, System.DateTimeOffset nextRetryAt, CancellationToken ct) => throw new System.NotImplementedException();
        public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct) => throw new System.NotImplementedException();
    }

    [Fact]
    public async Task DispatchAsync_SerializesAndEnqueues()
    {
        var store = new CapturingOutboxStore();
        var services = new ServiceCollection();
        services.AddSingleton<ISerializer<DispatchableCmd>>(new FakeSerializer());
        using var sp = services.BuildServiceProvider();

        var dispatcher = new OutboxSagaCommandDispatcher(store, sp);
        await dispatcher.DispatchAsync(new DispatchableCmd(42), CancellationToken.None);

        Assert.Equal("ZeroAlloc.Saga.Outbox.Tests.OutboxSagaCommandDispatcherTests+DispatchableCmd", store.CapturedTypeName);
        Assert.NotNull(store.CapturedPayload);
        Assert.Equal(4, store.CapturedPayload!.Length);
        Assert.Equal(42, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(store.CapturedPayload));
    }
}
```

**Step 3: Implement**

```csharp
// src/ZeroAlloc.Saga.Outbox/OutboxSagaCommandDispatcher.cs
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// <see cref="ISagaCommandDispatcher"/> that serializes the saga step command via
/// <see cref="ISerializer{T}"/> and writes it to <see cref="IOutboxStore"/>. The
/// outbox row commits atomically with the saga state save when both stores share
/// a transactional substrate (e.g. EfCore's scoped DbContext).
/// </summary>
public sealed class OutboxSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly IOutboxStore _store;
    private readonly System.IServiceProvider _services;

    public OutboxSagaCommandDispatcher(IOutboxStore store, System.IServiceProvider services)
    {
        _store = store;
        _services = services;
    }

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

**Step 4: Update PublicAPI.Unshipped.txt**

```
ZeroAlloc.Saga.Outbox.OutboxSagaCommandDispatcher
ZeroAlloc.Saga.Outbox.OutboxSagaCommandDispatcher.OutboxSagaCommandDispatcher(ZeroAlloc.Outbox.IOutboxStore! store, System.IServiceProvider! services) -> void
ZeroAlloc.Saga.Outbox.OutboxSagaCommandDispatcher.DispatchAsync<TCommand>(TCommand cmd, System.Threading.CancellationToken ct) -> System.Threading.Tasks.ValueTask
```

**Step 5: Run — expect pass**

**Step 6: Commit**

```bash
git add src/ZeroAlloc.Saga.Outbox/ tests/ZeroAlloc.Saga.Outbox.Tests/ ZeroAlloc.Saga.slnx
git commit -m "feat: outboxsagacommanddispatcher serializes via iserializer and enqueues"
```

---

## Task 12: `OutboxSagaCommandPoller` (`IHostedService`)

**Files:**
- Create: `src/ZeroAlloc.Saga.Outbox/OutboxSagaCommandPoller.cs`
- Modify: `src/ZeroAlloc.Saga.Outbox/PublicAPI.Unshipped.txt`
- Test: `tests/ZeroAlloc.Saga.Outbox.Tests/OutboxSagaCommandPollerTests.cs`

**Step 1: Failing test**

```csharp
[Fact]
public async Task Poller_FetchesPending_DispatchesViaRegistry_MarksSucceeded()
{
    // Build a fake outbox store with a pre-seeded pending entry.
    // Build a fake registry-callback that validates typeName + bytes and returns success.
    // Create poller, call ExecuteAsync once via reflection or via an exposed RunOnceAsync method,
    // assert the entry was marked succeeded.
    // Detail: the poller in this design relies on the GENERATED SagaCommandRegistry which only
    // exists in compilations where Saga is referenced AND a saga is declared. To keep this
    // test self-contained, the poller takes a delegate parameter that defaults to calling
    // SagaCommandRegistry.DispatchAsync. Test injects a custom delegate.
}
```

**Step 2: Implement**

The poller needs to call `SagaCommandRegistry.DispatchAsync` from the consumer's compilation. To make this testable without a real saga compilation, define a delegate:

```csharp
public delegate ValueTask SagaCommandRegistryDispatcher(
    string typeName,
    System.ReadOnlyMemory<byte> bytes,
    System.IServiceProvider services,
    IMediator mediator,
    CancellationToken ct);
```

The poller takes this delegate via constructor; the `WithOutbox()` extension wires up the default by reflectively finding `ZeroAlloc.Saga.Generated.SagaCommandRegistry.DispatchAsync` (which exists in the consumer's assembly because the saga generator emits it).

```csharp
// src/ZeroAlloc.Saga.Outbox/OutboxSagaCommandPoller.cs
public sealed class OutboxSagaCommandPoller : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopeFactory;
    private readonly SagaCommandRegistryDispatcher _dispatch;
    private readonly Microsoft.Extensions.Logging.ILogger<OutboxSagaCommandPoller> _log;
    private readonly OutboxSagaPollerOptions _options;

    public OutboxSagaCommandPoller(
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory,
        SagaCommandRegistryDispatcher dispatch,
        Microsoft.Extensions.Logging.ILogger<OutboxSagaCommandPoller> log,
        OutboxSagaPollerOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _dispatch = dispatch;
        _log = log;
        _options = options ?? new OutboxSagaPollerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PollOnceAsync(stoppingToken).ConfigureAwait(false); }
            catch (System.OperationCanceledException) { break; }
            catch (System.Exception ex) { _log.LogError(ex, "OutboxSagaCommandPoller cycle failed"); }
            try { await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false); }
            catch (System.OperationCanceledException) { break; }
        }
    }

    public async ValueTask PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var pending = await store.FetchPendingAsync(_options.BatchSize, ct).ConfigureAwait(false);
        foreach (var entry in pending)
        {
            try
            {
                await _dispatch(entry.TypeName, entry.Payload, scope.ServiceProvider, mediator, ct).ConfigureAwait(false);
                await store.MarkSucceededAsync(entry.Id, ct).ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                if (entry.RetryCount + 1 >= _options.MaxRetries)
                    await store.DeadLetterAsync(entry.Id, ex.ToString(), ct).ConfigureAwait(false);
                else
                    await store.MarkFailedAsync(entry.Id, entry.RetryCount + 1, System.DateTimeOffset.UtcNow.Add(_options.RetryDelay), ct).ConfigureAwait(false);
            }
        }
    }
}

public sealed class OutboxSagaPollerOptions
{
    public System.TimeSpan PollInterval { get; init; } = System.TimeSpan.FromSeconds(2);
    public int BatchSize { get; init; } = 32;
    public int MaxRetries { get; init; } = 5;
    public System.TimeSpan RetryDelay { get; init; } = System.TimeSpan.FromSeconds(10);
}
```

**Step 3: Run — expect pass**

**Step 4: Commit**

```bash
git add src/ZeroAlloc.Saga.Outbox/ tests/ZeroAlloc.Saga.Outbox.Tests/
git commit -m "feat: outboxsagacommandpoller as backgroundservice"
```

---

## Task 13: `WithOutbox()` extension

**Files:**
- Create: `src/ZeroAlloc.Saga.Outbox/SagaOutboxBuilderExtensions.cs`
- Modify: `src/ZeroAlloc.Saga.Outbox/PublicAPI.Unshipped.txt`
- Test: `tests/ZeroAlloc.Saga.Outbox.Tests/SagaOutboxRegistrationTests.cs`

**Step 1: Read Saga 1.1 builder type**

```bash
grep -rn "ISagaRegistrationBuilder\|class .*SagaBuilder\|public .*AddSaga" src/ZeroAlloc.Saga/*.cs | head
```

The exact type returned by `services.AddSaga<TSaga, TKey>()` is whatever Saga 1.1 already exposes. `WithOutbox()` is an extension on it.

**Step 2: Implement**

```csharp
// src/ZeroAlloc.Saga.Outbox/SagaOutboxBuilderExtensions.cs
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Outbox;

public static class SagaOutboxBuilderExtensions
{
    /// <summary>
    /// Replaces the default <see cref="ISagaCommandDispatcher"/> with
    /// <see cref="OutboxSagaCommandDispatcher"/> and registers the
    /// <see cref="OutboxSagaCommandPoller"/> as a hosted service.
    /// </summary>
    public static TBuilder WithOutbox<TBuilder>(this TBuilder builder)
        where TBuilder : ISagaRegistrationBuilder // adapt to actual builder interface
    {
        var services = builder.Services;
        services.Replace(ServiceDescriptor.Scoped<ISagaCommandDispatcher, OutboxSagaCommandDispatcher>());
        services.AddSingleton(_ => CreateRegistryDispatcher());
        services.AddHostedService<OutboxSagaCommandPoller>();
        return builder;
    }

    private static SagaCommandRegistryDispatcher CreateRegistryDispatcher()
    {
        // Discover the generator-emitted SagaCommandRegistry in the consumer's assembly.
        // The type lives in namespace ZeroAlloc.Saga.Generated and has a static
        // DispatchAsync(string, ReadOnlyMemory<byte>, IServiceProvider, IMediator, CancellationToken).
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("ZeroAlloc.Saga.Generated.SagaCommandRegistry", throwOnError: false);
            if (t is null) continue;
            var m = t.GetMethod(
                "DispatchAsync",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(System.ReadOnlyMemory<byte>), typeof(System.IServiceProvider), typeof(ZeroAlloc.Mediator.IMediator), typeof(System.Threading.CancellationToken) },
                modifiers: null);
            if (m is null) continue;
            return (typeName, bytes, sp, mediator, ct)
                => (System.Threading.Tasks.ValueTask)m.Invoke(null, new object[] { typeName, bytes, sp, mediator, ct })!;
        }
        throw new System.InvalidOperationException(
            "ZeroAlloc.Saga.Outbox.WithOutbox(): could not locate the generator-emitted ZeroAlloc.Saga.Generated.SagaCommandRegistry. " +
            "Ensure ZeroAlloc.Saga 1.2+ is referenced AND at least one [Saga]-decorated class exists in the compilation.");
    }
}
```

(The reflective discovery is one-time at startup. Cost is negligible. AOT consumers using this poller would need a `DynamicallyAccessedMembers` annotation — flag for AOT note in README.)

**Step 3: Failing test**

```csharp
[Fact]
public void WithOutbox_ReplacesDispatcher_AndAddsPoller()
{
    var services = new ServiceCollection();
    services.AddMediator();
    services.AddSaga<TestSaga, TestKey>().WithOutbox();
    var d = services.SingleOrDefault(x => x.ServiceType == typeof(ISagaCommandDispatcher));
    Assert.Equal(typeof(OutboxSagaCommandDispatcher), d!.ImplementationType);
    Assert.Contains(services, x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(OutboxSagaCommandPoller));
}
```

**Step 4: Run — expect pass**

**Step 5: Update PublicAPI.Unshipped.txt**

Add the `SagaOutboxBuilderExtensions` + `OutboxSagaPollerOptions` + `SagaCommandRegistryDispatcher` delegate entries.

**Step 6: Commit**

```bash
git add src/ZeroAlloc.Saga.Outbox/ tests/ZeroAlloc.Saga.Outbox.Tests/
git commit -m "feat: withoutbox extension wires dispatcher + poller"
```

---

## Task 14: E2E integration test (saga + outbox + EfCore atomic commit)

**Files:**
- Create: `tests/ZeroAlloc.Saga.Outbox.Tests/E2ETests.cs`
- Add reference to `Saga.EfCore`, `Outbox.EfCore` in test csproj

**Step 1: Failing test**

```csharp
[Fact]
public async Task Saga_DispatchesViaOutbox_CommittedAtomically_WithStateSave()
{
    // 1. Build a host with EfCore-based saga store + EfCore-based outbox store, both
    //    using the same DbContext.
    // 2. Trigger the saga's first event.
    // 3. Inside the handler, the dispatcher writes the outbox row (tracked, not yet saved).
    // 4. _store.SaveAsync persists both rows in one SaveChangesAsync.
    // 5. Verify the saga row exists AND the outbox row exists in the same TX.
    // 6. Run the poller once.
    // 7. Verify the command was dispatched via IMediator (capture via test handler).
    // 8. Verify the outbox row is now marked Succeeded.
}
```

(Detailed test body adapted from `tests/ZeroAlloc.Saga.EfCore.Tests/E2ETests.cs` — reuse fixtures.)

**Step 2: Run + commit**

```bash
git add tests/ZeroAlloc.Saga.Outbox.Tests/
git commit -m "test: e2e atomic dispatch through saga.outbox + saga.efcore"
```

---

## Task 15: OCC-conflict regression test (load-bearing)

**Files:**
- Add to: `tests/ZeroAlloc.Saga.Outbox.Tests/E2ETests.cs`

**Step 1: Test**

```csharp
[Fact]
public async Task OccConflict_RollsBackOutboxRow_NoDuplicateDispatch()
{
    // 1. Two parallel handlers race on the same saga key — second one OCC-conflicts.
    // 2. The losing handler's outbox row must NOT be visible to the poller.
    // 3. After retry, ONE outbox row exists.
    // 4. Poller drains, command dispatched exactly once.
    //
    // This is the test that fails on Saga 1.1 (without outbox bridge) because the
    // dispatch happens before save and is therefore not atomic with save's rollback.
}
```

This is the **load-bearing** test for the entire Phase 3a effort. It must fail without the outbox bridge and pass with it.

**Step 2: Run + commit**

```bash
git add tests/ZeroAlloc.Saga.Outbox.Tests/
git commit -m "test: occ-conflict no longer causes duplicate dispatch with outbox bridge"
```

---

## Task 16: Update README + persistence-efcore.md

**Files:**
- Modify: `README.md` (Saga repo)
- Modify: `docs/persistence-efcore.md`
- Create: `docs/outbox.md`

**Step 1: Update README's "v1.1 known limitations" section**

Strike the "OCC retry can dispatch twice" caveat (or rephrase: "Use `Saga.Outbox` for atomic dispatch — see docs/outbox.md").

**Step 2: Update Roadmap table**

Mark v1.2 (Phase 3a) as the just-shipped row. Adjust the other rows so v1.3, v1.4 etc. realign — the README's roadmap currently says v1.2 = Saga.Redis but BACKLOG.md said v1.2 = Outbox. Reconcile to whichever ordering the user prefers; default: this iteration is v1.2.

**Step 3: Create `docs/outbox.md`**

A 3–5 page guide:
- What it does (atomic dispatch).
- When to use it (always with EfCore; not needed with InMemory).
- Wiring example (DI).
- Marking command types `partial` + the ZASAGA016/017 diagnostics.
- The poller (cadence, batch size, retries, dead-letter behavior).
- Limitations: requires shared scoped DbContext between saga store and outbox store; cross-assembly commands need manual `[ZeroAllocSerializable]`.

**Step 4: Commit**

```bash
git add README.md docs/
git commit -m "docs: outbox bridge usage and known-limitation update"
```

---

## Task 17: CHANGELOG entry + bump release-please manifest expectation

**Files:**
- Modify: `CHANGELOG.md` (release-please will own it after merge, but a placeholder entry helps reviewers)

**Step 1: Note about release-please**

Don't write a CHANGELOG entry directly. release-please will compute the next version (1.1.0 → 1.2.0 from `feat:` commits, or → 2.0.0 from `feat!:` if any breaking change marker is present in this branch). All commits in this plan are non-breaking `feat:`/`fix:`/`docs:`/`test:`/`refactor:` — release-please will compute **1.2.0** for the runtime + a new 1.0.0 for `Saga.Outbox`.

If `release-please-config.json` is single-package + manifest pinned at 1.1.0, the new package needs to be added to the manifest so release-please publishes it. Update:

```json
{
  "packages": {
    ".": { "release-type": "simple" },
    "src/ZeroAlloc.Saga.Outbox": { "release-type": "simple" }
  }
}
```

(Or if the existing config is already multi-package, just add the new path.)

**Step 2: Update `.release-please-manifest.json`**

```json
{
  ".": "1.1.0",
  "src/ZeroAlloc.Saga.Outbox": "0.0.0"
}
```

(release-please will compute the first version — typically `1.0.0` for a new package on first feat: commit.)

**Step 3: Commit**

```bash
git add release-please-config.json .release-please-manifest.json
git commit -m "ci: register saga.outbox in release-please manifest"
```

---

## Task 18: Push + open PR

**Step 1: Final verification**

```bash
dotnet build ZeroAlloc.Saga.slnx -c Release --nologo
dotnet test tests/ZeroAlloc.Saga.Tests/ZeroAlloc.Saga.Tests.csproj -c Release --nologo
dotnet test tests/ZeroAlloc.Saga.Generator.Tests/ZeroAlloc.Saga.Generator.Tests.csproj -c Release --nologo
dotnet test tests/ZeroAlloc.Saga.Diagnostics.Tests/ZeroAlloc.Saga.Diagnostics.Tests.csproj -c Release --nologo
dotnet test tests/ZeroAlloc.Saga.EfCore.Tests/ZeroAlloc.Saga.EfCore.Tests.csproj -c Release --nologo
dotnet test tests/ZeroAlloc.Saga.Outbox.Tests/ZeroAlloc.Saga.Outbox.Tests.csproj -c Release --nologo
```

All must pass. AOT smoke (`dotnet publish samples/AotSmoke ...`) must still publish clean.

**Step 2: Push**

```bash
git push -u origin feat/v1.2-outbox-bridge-design
```

**Step 3: Open PR**

```bash
gh pr create --repo ZeroAlloc-Net/ZeroAlloc.Saga \
    --title "feat: phase 3a — outbox bridge for atomic command dispatch" \
    --body "$(cat <<'EOF'
## Summary

Implements [Phase 3a of the Saga roadmap](docs/plans/2026-05-04-saga-phase-3a-outbox-design.md). Closes the documented "command may dispatch twice on OCC retry" caveat from Saga 1.1.

- New `ISagaCommandDispatcher` indirection in Saga 1.2 — generator emits `_dispatcher.DispatchAsync(cmd)` instead of `_mediator.Send(cmd)`.
- New package `ZeroAlloc.Saga.Outbox` — `OutboxSagaCommandDispatcher`, `OutboxSagaCommandPoller`, `WithOutbox()` builder extension.
- Generator auto-applies `[ZeroAllocSerializable]` via partial-class extension when `ZeroAlloc.Serialisation` is referenced.
- Two new diagnostics: ZASAGA016 (warning + code-fix: `partial` required), ZASAGA017 (info: cross-assembly limitation).
- Per-compilation `SagaCommandRegistry` switch table for poller dispatch.

## Versioning

- `ZeroAlloc.Saga` 1.1.0 → 1.2.0 (feat).
- `ZeroAlloc.Saga.Outbox` 1.0.0 (new package).

## Test plan
- All 4 existing Saga test projects green (no regression).
- New `Saga.Outbox.Tests` project with unit + E2E + OCC-regression coverage.
- AOT smoke still publishes.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Definition of Done

- [ ] All 18 tasks committed.
- [ ] Full Saga test suite green (5 projects).
- [ ] AOT smoke publishes clean.
- [ ] PR open with all checks passing.
- [ ] Issue / roadmap entry referenced; will close on release-please ship of 1.2.0 + new package.
