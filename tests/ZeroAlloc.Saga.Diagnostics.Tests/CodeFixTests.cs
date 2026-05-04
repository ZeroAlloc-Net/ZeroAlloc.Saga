using System.Threading.Tasks;
using ZeroAlloc.Saga.Generator.CodeFixes;

namespace ZeroAlloc.Saga.Diagnostics.Tests;

/// <summary>
/// Verifies the three code-fix providers actually mutate the source toward a
/// shape that no longer triggers their respective diagnostic.
/// </summary>
public class CodeFixTests
{
    [Fact]
    public async Task ZASAGA001_MakePartial_FixAddsModifier()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record ReserveCommand(OrderId OrderId) : IRequest;

            [Saga]
            public class NotPartialSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;

        var fixedSrc = await CodeFixVerifier.ApplyFixAsync(src, new MakePartialCodeFixProvider(), "ZASAGA001");

        Assert.Contains("public partial class NotPartialSaga", fixedSrc);

        // After applying the fix the diagnostic should no longer fire.
        var run = await GeneratorVerifier.RunAsync(fixedSrc);
        Assert.DoesNotContain(run.Diagnostics, d => d.Id == "ZASAGA001");
    }

    [Fact]
    public async Task ZASAGA007_RenumberSteps_FixProducesContiguousOrder()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record StockReserved(OrderId OrderId) : INotification;
            public sealed record ReserveCommand(OrderId OrderId) : IRequest;
            public sealed record ChargeCommand(OrderId OrderId) : IRequest;

            [Saga]
            public partial class GappedSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 5)] public ChargeCommand Charge(StockReserved e) => new(e.OrderId);
            }
            """;

        var fixedSrc = await CodeFixVerifier.ApplyFixAsync(src, new RenumberStepsCodeFixProvider(), "ZASAGA007");

        Assert.Contains("Order = 1", fixedSrc);
        Assert.Contains("Order = 2", fixedSrc);
        Assert.DoesNotContain("Order = 5", fixedSrc);

        var run = await GeneratorVerifier.RunAsync(fixedSrc);
        Assert.DoesNotContain(run.Diagnostics, d => d.Id == "ZASAGA007");
    }

    [Fact]
    public async Task ZASAGA016_AddPartialModifier_FixAddsPartialKeyword()
    {
        // The full-source pattern (mirroring the ZASAGA016 diagnostic test
        // in DiagnosticTests.cs) — we need both the Serialisation stub
        // namespace (to trip the gate) and a saga referencing a non-partial
        // step command type.
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace ZeroAlloc.Serialisation
            {
                [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
                public sealed class ZeroAllocSerializableAttribute : System.Attribute { }
            }

            namespace Sample
            {
                public readonly record struct OrderId(int V) : IEquatable<OrderId>;
                public sealed record OrderPlaced(OrderId OrderId) : INotification;
                public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

                [Saga]
                public partial class TwoStepSaga
                {
                    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                    [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
                }
            }
            """;

        var fixedSrc = await CodeFixVerifier.ApplyFixAsync(src, new AddPartialModifierCodeFix(), "ZASAGA016");

        // The fix should turn the record struct declaration into a partial.
        Assert.Contains("partial record struct ReserveCmd", fixedSrc);

        // After applying the fix the diagnostic must no longer fire.
        var run = await GeneratorVerifier.RunAsync(fixedSrc);
        Assert.DoesNotContain(run.Diagnostics, d => d.Id == "ZASAGA016");
    }

    [Fact]
    public async Task ZASAGA009_AddMissingCompensation_FixAddsStubMethod()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record PaymentDeclined(OrderId OrderId) : INotification;
            public sealed record ReserveCommand(OrderId OrderId) : IRequest;

            [Saga]
            public partial class MissingCompSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;
                [Step(Order = 1, Compensate = nameof(Refund), CompensateOn = typeof(PaymentDeclined))]
                public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;

        var fixedSrc = await CodeFixVerifier.ApplyFixAsync(src, new AddMissingCompensationMethodCodeFixProvider(), "ZASAGA009");

        Assert.Contains("Refund()", fixedSrc);

        var run = await GeneratorVerifier.RunAsync(fixedSrc);
        Assert.DoesNotContain(run.Diagnostics, d => d.Id == "ZASAGA009");
    }
}
