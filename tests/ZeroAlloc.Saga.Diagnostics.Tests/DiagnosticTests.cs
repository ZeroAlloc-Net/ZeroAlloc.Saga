using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Diagnostics.Tests;

/// <summary>
/// One test per ZASAGA0XX. Each composes a deliberately-broken saga, runs the
/// generator, and asserts the expected diagnostic ID was reported.
/// </summary>
public class DiagnosticTests
{
    private const string Header = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;

        namespace Sample;

        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public readonly record struct CustomerId(string V) : IEquatable<CustomerId>;

        public sealed record OrderPlaced(OrderId OrderId) : INotification;
        public sealed record StockReserved(OrderId OrderId) : INotification;
        public sealed record PaymentDeclined(OrderId OrderId) : INotification;

        public sealed record ReserveCommand(OrderId OrderId) : IRequest;
        public sealed record ChargeCommand(OrderId OrderId) : IRequest;
        public sealed record RefundCommand(OrderId OrderId) : IRequest;

        """;

    [Fact]
    public Task ZASAGA001_NonPartial_Class_ReportsError()
    {
        var src = Header + """
            [Saga]
            public class NotPartialSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA001");
    }

    [Fact]
    public Task ZASAGA002_AbstractClass_ReportsError()
    {
        var src = Header + """
            [Saga]
            public abstract partial class AbstractSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA002");
    }

    [Fact]
    public Task ZASAGA003_NoParameterlessCtor_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class NoCtorSaga
            {
                public NoCtorSaga(int x) { }
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA003");
    }

    [Fact]
    public Task ZASAGA004_StepEventLacksCorrelationKey_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class MissingCorrSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
                // StockReserved is consumed but has no [CorrelationKey] mapping.
                [Step(Order = 2)] public ChargeCommand Charge(StockReserved e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA004");
    }

    [Fact]
    public Task ZASAGA005_InconsistentCorrelationKeyTypes_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class MixedKeySaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public CustomerId CorrelationOther(StockReserved e) => new("x");
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA005");
    }

    [Fact]
    public Task ZASAGA006_CorrelationKeyBadSignature_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class BadCorrSigSaga
            {
                [CorrelationKey] public OrderId Correlation() => default; // no event parameter
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA006");
    }

    [Fact]
    public Task ZASAGA007_StepOrderGaps_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class GappedSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 5)] public ChargeCommand Charge(StockReserved e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA007");
    }

    [Fact]
    public Task ZASAGA008_StepBadSignature_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class BadStepSigSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public void Reserve(OrderPlaced e, int extra) { } // wrong shape
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA008");
    }

    [Fact]
    public Task ZASAGA009_CompensateMethodMissing_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class MissingCompSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;
                [Step(Order = 1, Compensate = nameof(DoesNotExist), CompensateOn = typeof(PaymentDeclined))]
                public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA009");
    }

    [Fact]
    public Task ZASAGA010_CompensateOnEventLacksCorrelationKey_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class CompOnNoCorrSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                // PaymentDeclined has no [CorrelationKey] mapping.
                [Step(Order = 1, Compensate = nameof(Refund), CompensateOn = typeof(PaymentDeclined))]
                public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
                public RefundCommand Refund() => new(default);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA010");
    }

    [Fact]
    public Task ZASAGA011_CorrelationKeyMutatesState_ReportsWarning()
    {
        var src = Header + """
            [Saga]
            public partial class MutatingCorrSaga
            {
                public OrderId LastSeen { get; private set; }
                [CorrelationKey]
                public OrderId Correlation(OrderPlaced e)
                {
                    LastSeen = e.OrderId;     // mutation in correlation key extraction
                    return e.OrderId;
                }
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA011");
    }

    [Fact]
    public Task ZASAGA012_CompensateWithoutCompensateOn_ReportsWarning()
    {
        var src = Header + """
            [Saga]
            public partial class DeadCompSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1, Compensate = nameof(Refund))]
                public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
                public RefundCommand Refund() => new(default);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA012");
    }

    [Fact]
    public Task ZASAGA013_TwoSagasSameEventDifferentKeys_ReportsWarning()
    {
        var src = Header + """
            [Saga]
            public partial class SagaA
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }

            [Saga]
            public partial class SagaB
            {
                [CorrelationKey] public CustomerId Correlation(OrderPlaced e) => new("x");
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA013");
    }

    // ── ZASAGA016 ───────────────────────────────────────────────────────────
    // Header used by the ZASAGA016 tests. Mirrors the canonical Header above
    // but inlines a stub for ZeroAlloc.Serialisation.ZeroAllocSerializableAttribute
    // so the generator's `serialisationReferenced` gate is tripped without
    // needing to add the real package as a test dependency.
    private const string SerialisationStubHeader = """
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
        }

        """;

    [Fact]
    public async Task ZASAGA016_FiresWhen_StepCommandType_IsNotPartial_AndSerialisationStubPresent()
    {
        var src = SerialisationStubHeader + """
            namespace Sample
            {
                public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

                [Saga]
                public partial class TwoStepSaga
                {
                    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                    [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
                }
            }
            """;
        var run = await GeneratorVerifier.RunAsync(src);
        Assert.Contains(run.Diagnostics, d => d.Id == "ZASAGA016");
    }

    [Fact]
    public async Task ZASAGA016_DoesNotFire_WhenStepCommandIsPartial()
    {
        var src = SerialisationStubHeader + """
            namespace Sample
            {
                public partial record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

                [Saga]
                public partial class TwoStepSaga
                {
                    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                    [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
                }
            }
            """;
        var run = await GeneratorVerifier.RunAsync(src);
        Assert.DoesNotContain(run.Diagnostics, d => d.Id == "ZASAGA016");
    }

    [Fact]
    public async Task ZASAGA016_DoesNotFire_WhenSerialisationNotReferenced()
    {
        // Same shape as the firing case but with NO ZeroAllocSerializableAttribute
        // in the compilation — the gate stays closed and ZASAGA016 must not fire.
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class TwoStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        var run = await GeneratorVerifier.RunAsync(src);
        Assert.DoesNotContain(run.Diagnostics, d => d.Id == "ZASAGA016");
    }
}
