using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ZeroAlloc.Saga.Diagnostics.Tests;

/// <summary>
/// The generator's cached models carry source locations, so an edit that does not touch a saga must
/// leave every tracked step and output cached, and an edit that moves a saga must move its diagnostic.
/// </summary>
public class IncrementalityTests
{
    private const string SagasSource = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;

        namespace Sample;

        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public sealed record OrderPlaced(OrderId OrderId) : INotification;
        public sealed record PaymentDeclined(OrderId OrderId) : INotification;
        public sealed partial record ReserveCommand(OrderId OrderId) : IRequest;
        public sealed partial record RefundCommand(OrderId OrderId) : IRequest;

        [Saga]
        public partial class OrderSaga
        {
            public int Attempts { get; set; }
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;
            [Step(Order = 1, Compensate = nameof(Refund), CompensateOn = typeof(PaymentDeclined))]
            public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            public RefundCommand Refund() => new(default);
        }

        // Reports ZASAGA012, so the run has a diagnostic to keep in place.
        [Saga]
        public partial class DeadCompSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1, Compensate = nameof(Refund))]
            public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            public RefundCommand Refund() => new(default);
        }

        public static class Startup
        {
            public static void Configure() => Marker.WithEfCoreStore<object>();
        }
        public static class Marker
        {
            public static void WithEfCoreStore<T>() { }
        }
        """;

    // The tracking names the generator gives its steps, in ZeroAlloc.Saga.Generator.TrackingNames.
    private static readonly string[] GeneratorStepNames =
    [
        "SagaModels", "AllSagaModels", "SerialisationReferenced", "DurableBackendReferenced",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedEdit_LeavesEveryTrackedStepAndOutputCached(bool referencesSerialisation)
    {
        var sagas = CSharpSyntaxTree.ParseText(SagasSource, path: "/src/Sagas.cs");
        var unrelated = CSharpSyntaxTree.ParseText(
            "namespace Sample; public class Unrelated { public int M() => 1; }", path: "/src/Unrelated.cs");
        var extra = referencesSerialisation
            ? new[]
            {
                GeneratorVerifier.CompileToReference("""
                    namespace ZeroAlloc.Serialisation;

                    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
                    public sealed class ZeroAllocSerializableAttribute : System.Attribute { }
                    """, "ZeroAlloc.Serialisation"),
            }
            : null;
        var compilation = GeneratorVerifier.CreateCompilation(new[] { sagas, unrelated }, extra);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new ZeroAlloc.Saga.Generator.SagaGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        var first = driver.GetRunResult().Results[0];

        var edited = compilation.ReplaceSyntaxTree(
            unrelated,
            unrelated.WithChangedText(SourceText.From(
                "namespace Sample; public class Unrelated { public int M() => 2; public int N() => 3; }")));
        driver = driver.RunGenerators(edited);
        var second = driver.GetRunResult().Results[0];

        // Roslyn's own steps, such as the one that pairs each tree with the compilation, rerun on
        // every edit; the generator's named steps must not.
        foreach (var name in GeneratorStepNames)
        {
            Assert.True(second.TrackedSteps.TryGetValue(name, out var runSteps), $"Step '{name}' was not tracked.");
            AssertAllCachedOrUnchanged(name, runSteps);
        }

        Assert.NotEmpty(second.TrackedOutputSteps);
        foreach (var step in second.TrackedOutputSteps)
        {
            AssertAllCachedOrUnchanged(step.Key, step.Value);
        }

        // A cached output still reports its diagnostics at the same place.
        Assert.Equal(Describe(first.Diagnostics), Describe(second.Diagnostics));
        Assert.Contains(second.Diagnostics, d => string.Equals(d.Id, "ZASAGA012", StringComparison.Ordinal));
        Assert.Contains(second.Diagnostics, d => string.Equals(d.Id, "ZASAGA015", StringComparison.Ordinal));
    }

    [Fact]
    public void EditAboveASaga_MovesItsDiagnostic()
    {
        var sagas = CSharpSyntaxTree.ParseText(SagasSource, path: "/src/Sagas.cs");
        var compilation = GeneratorVerifier.CreateCompilation(new[] { sagas });

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ZeroAlloc.Saga.Generator.SagaGenerator());
        driver = driver.RunGenerators(compilation);
        var before = Assert.Single(
            driver.GetRunResult().Diagnostics, d => string.Equals(d.Id, "ZASAGA012", StringComparison.Ordinal));

        var moved = sagas.WithChangedText(SourceText.From(
            SagasSource.Replace("namespace Sample;", "namespace Sample;\n\n// two\n// more lines", StringComparison.Ordinal)));
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(sagas, moved));
        var after = Assert.Single(
            driver.GetRunResult().Diagnostics, d => string.Equals(d.Id, "ZASAGA012", StringComparison.Ordinal));

        Assert.Equal(
            before.Location.GetLineSpan().StartLinePosition.Line + 3,
            after.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Same(moved, after.Location.SourceTree);
    }

    private static void AssertAllCachedOrUnchanged(string stepName, ImmutableArray<IncrementalGeneratorRunStep> runSteps)
    {
        foreach (var runStep in runSteps)
        {
            foreach (var (_, reason) in runStep.Outputs)
            {
                Assert.True(
                    reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                    $"Step '{stepName}' reran with reason {reason}.");
            }
        }
    }

    private static string[] Describe(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics
            .Select(d => d.Id + " " + d.Location.GetLineSpan())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
}
