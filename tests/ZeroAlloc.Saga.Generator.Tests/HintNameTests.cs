using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Saga.Generator.Tests;

/// <summary>
/// A generated file is named after its saga's namespace and containing types, each with its
/// generic arity, and a handler file also after its event's, so two same-named sagas or events
/// never produce the same hint name. A duplicate hint name made the generator throw CS8785, and
/// then no saga in the project was generated.
/// </summary>
public class HintNameTests
{
    private const string Header = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;
        """;

    private const string Contracts = """
        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public sealed record OrderPlaced(OrderId OrderId) : INotification;
        public sealed record ReserveStockCommand(OrderId OrderId) : IRequest;
        """;

    private const string Body = """
        {
            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
        }
        """;

    [Fact]
    public void SameNamedSagas_InDifferentNamespaces_AreBothGenerated()
    {
        var src = Header + $$"""

            namespace N1 { {{Contracts}} [Saga] public partial class OrderSaga {{Body}} }
            namespace N2 { {{Contracts}} [Saga] public partial class OrderSaga {{Body}} }
            """;

        var result = Run(src);

        Assert.Equal(
            [
                "GeneratedSagaCommandSource.g.cs", "MediatorSagaCommandDispatcher.g.cs",
                "N1.OrderSaga.BuilderExtensions.g.cs", "N1.OrderSaga.CorrelationDispatch.g.cs",
                "N1.OrderSaga.Fsm.g.cs", "N1.OrderSaga.Handler.N1.OrderPlaced.g.cs",
                "N1.OrderSaga.PersistableState.g.cs", "N1.OrderSaga.g.cs",
                "N2.OrderSaga.BuilderExtensions.g.cs", "N2.OrderSaga.CorrelationDispatch.g.cs",
                "N2.OrderSaga.Fsm.g.cs", "N2.OrderSaga.Handler.N2.OrderPlaced.g.cs",
                "N2.OrderSaga.PersistableState.g.cs", "N2.OrderSaga.g.cs",
            ],
            HintNames(result));
    }

    [Fact]
    public void SameNamedSagas_InDifferentContainingTypes_GetDistinctHintNames()
    {
        // A nested saga is reported as ZASAGA002, but it must not take down every other saga.
        var src = Header + $$"""

            namespace App
            {
                {{Contracts}}
                public partial class Orders { [Saga] public partial class OrderSaga {{Body}} }
                public partial class Customers { public partial class Inner { [Saga] public partial class OrderSaga {{Body}} } }
                [Saga] public partial class OrderSaga {{Body}}
            }
            """;

        var result = Run(src);

        var names = HintNames(result);
        Assert.Contains("App.OrderSaga.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.Orders+OrderSaga.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.Customers+Inner+OrderSaga.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.Customers+Inner+OrderSaga.Handler.App.OrderPlaced.g.cs", names, StringComparer.Ordinal);
    }

    [Fact]
    public void GenericSagas_AreNamedWithTheirArity()
    {
        var src = Header + $$"""

            namespace App
            {
                {{Contracts}}
                [Saga] public partial class OrderSaga {{Body}}
                [Saga] public partial class OrderSaga<T> {{Body}}
                public partial class Outer<T> { [Saga] public partial class OrderSaga {{Body}} }
            }
            """;

        var result = Run(src);

        var names = HintNames(result);
        Assert.Contains("App.OrderSaga.Fsm.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.OrderSaga`1.Fsm.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.Outer`1+OrderSaga.Fsm.g.cs", names, StringComparer.Ordinal);
    }

    [Fact]
    public void SameNamedEvents_InDifferentNamespaces_GetDistinctHandlerHintNames()
    {
        // Only the hint names are checked here. SnapshotTests.SameNamedEvents_InOneSaga covers
        // the handler class names and FSM triggers of such events, #216.
        var src = Header + """

            namespace A { public sealed record Placed(int Id) : INotification; }
            namespace B { public sealed record Placed(int Id) : INotification; }
            namespace App
            {
                public sealed record Step1(int Id) : IRequest;
                public sealed record Step2(int Id) : IRequest;
                [Saga]
                public partial class OrderSaga
                {
                    [CorrelationKey] public int Key(A.Placed e) => e.Id;
                    [CorrelationKey] public int Key(B.Placed e) => e.Id;
                    [Step(Order = 1)] public Step1 First(A.Placed e) => new(e.Id);
                    [Step(Order = 2)] public Step2 Second(B.Placed e) => new(e.Id);
                }
            }
            """;

        var result = Run(src);

        var names = HintNames(result);
        Assert.Contains("App.OrderSaga.Handler.A.Placed.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.OrderSaga.Handler.B.Placed.g.cs", names, StringComparer.Ordinal);
    }

    [Fact]
    public void CompensateOnHandler_IsNamedAfterItsEvent()
    {
        var src = Header + """

            namespace App
            {
                public sealed record OrderPlaced(int Id) : INotification;
                public sealed record StockReserved(int Id) : INotification;
                public sealed record PaymentDeclined(int Id) : INotification;
                public sealed record ReserveStock(int Id) : IRequest;
                public sealed record ReleaseStock(int Id) : IRequest;
                public sealed record Charge(int Id) : IRequest;
                [Saga]
                public partial class OrderSaga
                {
                    [CorrelationKey] public int Key(OrderPlaced e) => e.Id;
                    [CorrelationKey] public int Key(StockReserved e) => e.Id;
                    [CorrelationKey] public int Key(PaymentDeclined e) => e.Id;
                    [Step(Order = 1, Compensate = nameof(Release))] public ReserveStock Reserve(OrderPlaced e) => new(e.Id);
                    public ReleaseStock Release() => new(0);
                    [Step(Order = 2, CompensateOn = typeof(PaymentDeclined))] public Charge Pay(StockReserved e) => new(e.Id);
                }
            }
            """;

        var result = Run(src);

        var names = HintNames(result);
        Assert.Contains("App.OrderSaga.Handler.App.OrderPlaced.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.OrderSaga.Handler.App.StockReserved.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.OrderSaga.Handler.App.PaymentDeclined.g.cs", names, StringComparer.Ordinal);
    }

    [Fact]
    public void ConstructedGenericEvents_AreNamedWithTheirTypeArguments()
    {
        var src = Header + """

            namespace App
            {
                public sealed record Envelope<T>(int Id) : INotification;
                public sealed record Step1(int Id) : IRequest;
                public sealed record Step2(int Id) : IRequest;
                [Saga]
                public partial class OrderSaga
                {
                    [CorrelationKey] public int Key(Envelope<int> e) => e.Id;
                    [CorrelationKey] public int Key(Envelope<string> e) => e.Id;
                    [Step(Order = 1)] public Step1 First(Envelope<int> e) => new(e.Id);
                    [Step(Order = 2)] public Step2 Second(Envelope<string> e) => new(e.Id);
                }
            }
            """;

        var result = Run(src);

        var names = HintNames(result);
        Assert.Contains("App.OrderSaga.Handler.App.Envelope`1[System.Int32].g.cs", names, StringComparer.Ordinal);
        Assert.Contains("App.OrderSaga.Handler.App.Envelope`1[System.String].g.cs", names, StringComparer.Ordinal);
    }

    [Fact]
    public void VerbatimAndNonAsciiNames_AreNamedByTheirIdentifier()
    {
        // A verbatim identifier is named without its '@', and a letter outside ASCII is kept.
        var src = Header + $$"""

            namespace @class.Café { {{Contracts}} [Saga] public partial class ΩmegaSaga {{Body}} }
            """;

        var result = Run(src);

        var names = HintNames(result);
        Assert.Contains("class.Café.ΩmegaSaga.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("class.Café.ΩmegaSaga.Handler.class.Café.OrderPlaced.g.cs", names, StringComparer.Ordinal);
    }

    [Fact]
    public void SagaInGlobalNamespace_IsNamedWithoutANamespacePrefix()
    {
        var src = Header + $$"""

            {{Contracts}}
            [Saga] public partial class OrderSaga {{Body}}
            """;

        var result = Run(src);

        var names = HintNames(result);
        Assert.Contains("OrderSaga.g.cs", names, StringComparer.Ordinal);
        Assert.Contains("OrderSaga.Handler.OrderPlaced.g.cs", names, StringComparer.Ordinal);
    }

    private static GeneratorRunResult Run(string source)
    {
        var result = GeneratorTestHost.Run(source).GetRunResult().Results[0];
        Assert.Null(result.Exception);
        Assert.DoesNotContain(result.Diagnostics, d => string.Equals(d.Id, "CS8785", StringComparison.Ordinal));
        return result;
    }

    private static string[] HintNames(GeneratorRunResult result) =>
        [.. result.GeneratedSources.Select(s => s.HintName).Order(StringComparer.Ordinal)];
}
