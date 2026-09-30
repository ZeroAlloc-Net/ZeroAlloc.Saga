using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ZeroAlloc.Saga.Diagnostics.Tests;

/// <summary>
/// Every ZASAGA diagnostic is reported at the class, member or type it is about, as a source
/// location bound to the syntax tree, so the IDE can point at it and <c>#pragma</c> can suppress it.
/// A source marked with [| and |] gives the expected span; the markers are removed before it runs.
/// </summary>
public class DiagnosticLocationTests
{
    private const string Header = """
        using System;
        using System.Collections.Generic;
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

    // Trips the generator's ZeroAlloc.Serialisation gate without the real package.
    private static readonly MetadataReference SerialisationStub = GeneratorVerifier.CompileToReference("""
        namespace ZeroAlloc.Serialisation;

        [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
        public sealed class ZeroAllocSerializableAttribute : System.Attribute { }
        """, "ZeroAlloc.Serialisation");

    // A call the generator reads as a durable backend being wired, which turns on ZASAGA015.
    private const string DurableBackendShim = """

        public static class StartupShim
        {
            public static void Configure() => Marker.WithEfCoreStore<object>();
        }
        public static class Marker
        {
            public static void WithEfCoreStore<T>() { }
        }
        """;

    public static TheoryData<string, string> Cases() => new()
    {
        // ZASAGA001: the class identifier.
        { "ZASAGA001", Header + """
            [Saga]
            public class [|NotPartialSaga|]
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA002: the class identifier.
        { "ZASAGA002", Header + """
            [Saga]
            public abstract partial class [|AbstractSaga|]
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA003: the class identifier.
        { "ZASAGA003", Header + """
            [Saga]
            public partial class [|NoCtorSaga|]
            {
                public NoCtorSaga(int x) { }
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA004: the step whose event has no [CorrelationKey].
        { "ZASAGA004", Header + """
            [Saga]
            public partial class MissingCorrSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 2)] public ChargeCommand [|Charge|](StockReserved e) => new(e.OrderId);
            }
            """ },
        // ZASAGA005: the [CorrelationKey] method whose key type differs from the first.
        { "ZASAGA005", Header + """
            [Saga]
            public partial class MixedKeySaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public CustomerId [|CorrelationOther|](StockReserved e) => new("x");
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA006: the [CorrelationKey] method.
        { "ZASAGA006", Header + """
            [Saga]
            public partial class BadCorrSigSaga
            {
                [CorrelationKey] public OrderId [|Correlation|]() => default;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA007: the class identifier.
        { "ZASAGA007", Header + """
            [Saga]
            public partial class [|GappedSaga|]
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 5)] public ChargeCommand Charge(StockReserved e) => new(e.OrderId);
            }
            """ },
        // ZASAGA008: the step method.
        { "ZASAGA008", Header + """
            [Saga]
            public partial class BadStepSigSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public void [|Reserve|](OrderPlaced e, int extra) { }
            }
            """ },
        // ZASAGA009: the step method naming the missing compensation.
        { "ZASAGA009", Header + """
            [Saga]
            public partial class MissingCompSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;
                [Step(Order = 1, Compensate = "DoesNotExist", CompensateOn = typeof(PaymentDeclined))]
                public ReserveCommand [|Reserve|](OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA010: the step method whose CompensateOn event has no [CorrelationKey].
        { "ZASAGA010", Header + """
            [Saga]
            public partial class CompOnNoCorrSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1, Compensate = nameof(Refund), CompensateOn = typeof(PaymentDeclined))]
                public ReserveCommand [|Reserve|](OrderPlaced e) => new(e.OrderId);
                public RefundCommand Refund() => new(default);
            }
            """ },
        // ZASAGA011: the [CorrelationKey] method that mutates state.
        { "ZASAGA011", Header + """
            [Saga]
            public partial class MutatingCorrSaga
            {
                public OrderId LastSeen { get; private set; }
                [CorrelationKey]
                public OrderId [|Correlation|](OrderPlaced e)
                {
                    LastSeen = e.OrderId;
                    return e.OrderId;
                }
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA012: the step method whose compensation can never run.
        { "ZASAGA012", Header + """
            [Saga]
            public partial class DeadCompSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1, Compensate = nameof(Refund))]
                public ReserveCommand [|Reserve|](OrderPlaced e) => new(e.OrderId);
                public RefundCommand Refund() => new(default);
            }
            """ },
        // ZASAGA013: the [CorrelationKey] method of the second saga, the one that conflicts.
        { "ZASAGA013", Header + """
            [Saga]
            public partial class SagaA
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }

            [Saga]
            public partial class SagaB
            {
                [CorrelationKey] public CustomerId [|Correlation|](OrderPlaced e) => new("x");
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA014: the state member whose type is unsupported.
        { "ZASAGA014", Header + """
            [Saga]
            public partial class ListFieldSaga
            {
                public List<string> [|Items|] { get; set; } = new();
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ },
        // ZASAGA015: the saga class identifier.
        { "ZASAGA015", Header + """
            [Saga]
            public partial class [|IdemSaga|]
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """ + DurableBackendShim },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Diagnostic_IsReportedAtItsSubject(string id, string marked)
    {
        var (source, spans) = Unmark(marked);
        var expected = Assert.Single(spans);

        var diagnostics = GeneratorVerifier.RunOnFile(source);

        var diagnostic = Assert.Single(diagnostics, d => string.Equals(d.Id, id, StringComparison.Ordinal));
        AssertAt(diagnostic.Location, source, expected);
    }

    [Fact]
    public void ZASAGA017_IsReportedAtTheStepReturnTypeThatNamesTheForeignCommand()
    {
        var foreign = GeneratorVerifier.CompileToReference("""
            using System;
            using ZeroAlloc.Mediator;

            namespace Foreign;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;
            """, "Foreign.Asm");

        var (source, spans) = Unmark("""
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;
            using Foreign;

            namespace Sample;

            public sealed record OrderPlaced(OrderId OrderId) : INotification;

            [Saga]
            public partial class CrossAsmSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public [|ReserveCmd|] Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """);

        var diagnostics = GeneratorVerifier.RunOnFile(source, new[] { foreign, SerialisationStub });

        var diagnostic = Assert.Single(diagnostics, d => string.Equals(d.Id, "ZASAGA017", StringComparison.Ordinal));
        AssertAt(diagnostic.Location, source, Assert.Single(spans));
    }

    [Fact]
    public void ZASAGA013_NamesTheFirstSagaCorrelationMethodAsAnAdditionalLocation()
    {
        var (source, spans) = Unmark(Header + """
            [Saga]
            public partial class SagaA
            {
                [CorrelationKey] public OrderId [|Correlation|](OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }

            [Saga]
            public partial class SagaB
            {
                [CorrelationKey] public CustomerId [|Correlation|](OrderPlaced e) => new("x");
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """);

        var diagnostics = GeneratorVerifier.RunOnFile(source);

        var diagnostic = Assert.Single(diagnostics, d => string.Equals(d.Id, "ZASAGA013", StringComparison.Ordinal));
        AssertAt(diagnostic.Location, source, spans[1]);
        AssertAt(Assert.Single(diagnostic.AdditionalLocations), source, spans[0]);
    }

    [Theory]
    // ZASAGA012 is reported from the per-saga model; ZASAGA015 from the compilation-wide step,
    // and its own message tells users to suppress it with #pragma.
    [InlineData("ZASAGA012", """
        [Saga]
        public partial class QuietSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
        #pragma warning disable ZASAGA012
            [Step(Order = 1, Compensate = nameof(Refund))]
            public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
        #pragma warning restore ZASAGA012
            public RefundCommand Refund() => new(default);
        }

        [Saga]
        public partial class LoudSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1, Compensate = nameof(Refund))]
            public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            public RefundCommand Refund() => new(default);
        }
        """)]
    [InlineData("ZASAGA015", """
        #pragma warning disable ZASAGA015
        [Saga]
        public partial class QuietSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
        }
        #pragma warning restore ZASAGA015

        [Saga]
        public partial class LoudSaga
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
        }
        """ + DurableBackendShim)]
    public void PragmaAroundOneSaga_SuppressesThatDiagnosticOnly(string id, string body)
    {
        var source = Header + body;
        var diagnostics = GeneratorVerifier.RunOnFile(source);

        var reported = diagnostics.Where(d => string.Equals(d.Id, id, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, reported.Count);
        Assert.True(InSaga("QuietSaga").IsSuppressed, $"{id} in QuietSaga was not suppressed by #pragma.");
        Assert.False(InSaga("LoudSaga").IsSuppressed);

        // Matches by the line the diagnostic points at, or by the saga named in the message when it
        // has no location, so a wrong location fails on suppression rather than on lookup.
        Diagnostic InSaga(string saga)
        {
            var sagaDeclaration = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source).GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Single(c => string.Equals(c.Identifier.ValueText, saga, StringComparison.Ordinal));
            var lines = sagaDeclaration.SyntaxTree.GetLineSpan(sagaDeclaration.Span).Span;

            return Assert.Single(reported, d => d.Location == Location.None
                ? d.GetMessage(CultureInfo.InvariantCulture).Contains("'" + saga + "'", StringComparison.Ordinal)
                : d.Location.GetLineSpan().StartLinePosition.Line >= lines.Start.Line
                    && d.Location.GetLineSpan().StartLinePosition.Line <= lines.End.Line);
        }
    }

    internal static void AssertAt(Location location, string source, TextSpan expected)
    {
        // A source location, bound to the tree, is what #pragma and the IDE need.
        Assert.Equal(LocationKind.SourceFile, location.Kind);
        Assert.NotNull(location.SourceTree);
        Assert.Equal(GeneratorVerifier.TestFilePath, location.SourceTree!.FilePath);
        Assert.Equal(expected, location.SourceSpan);

        var lineSpan = location.GetLineSpan();
        Assert.Equal(GeneratorVerifier.TestFilePath, lineSpan.Path);
        Assert.Equal(SourceText.From(source).Lines.GetLinePositionSpan(expected), lineSpan.Span);
    }

    internal static (string Source, List<TextSpan> Spans) Unmark(string marked)
    {
        var sb = new StringBuilder(marked.Length);
        var spans = new List<TextSpan>();
        var start = -1;
        for (var i = 0; i < marked.Length; i++)
        {
            if (string.CompareOrdinal(marked, i, "[|", 0, 2) == 0)
            {
                start = sb.Length;
                i++;
            }
            else if (string.CompareOrdinal(marked, i, "|]", 0, 2) == 0)
            {
                spans.Add(TextSpan.FromBounds(start, sb.Length));
                i++;
            }
            else
            {
                sb.Append(marked[i]);
            }
        }

        return (sb.ToString(), spans);
    }
}
