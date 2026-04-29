using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Diagnostics.Tests;

/// <summary>
/// Coverage for ZASAGA014 (unsupported saga state field type) and ZASAGA015
/// (idempotency hint when a durable backend is wired). Both are emitted by
/// SagaGenerator at compile time.
/// </summary>
public class StateFieldDiagnosticTests
{
    private const string Header = """
        using System;
        using System.Collections.Generic;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;

        namespace Sample;

        public readonly record struct OrderId(int V) : IEquatable<OrderId>;
        public sealed record OrderPlaced(OrderId OrderId) : INotification;
        public sealed record ReserveCommand(OrderId OrderId) : IRequest;

        """;

    [Fact]
    public Task ZASAGA014_ListField_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class ListFieldSaga
            {
                public List<string> Items { get; set; } = new();
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA014");
    }

    [Fact]
    public Task ZASAGA014_DictionaryField_ReportsError()
    {
        var src = Header + """
            [Saga]
            public partial class DictionaryFieldSaga
            {
                public Dictionary<string, int> Counts { get; set; } = new();
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA014");
    }

    [Fact]
    public Task ZASAGA014_CustomRecordField_ReportsError()
    {
        var src = Header + """
            public sealed record Address(string Street, string City);

            [Saga]
            public partial class CustomRecordFieldSaga
            {
                public Address ShipTo { get; set; } = new("", "");
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA014");
    }

    [Fact]
    public async Task ZASAGA014_NotSagaState_AttributeSuppressesError()
    {
        var src = Header + """
            [Saga]
            public partial class WithExclusionSaga
            {
                [NotSagaState]
                public List<string> Diagnostics { get; set; } = new();

                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        var run = await GeneratorVerifier.RunAsync(src);
        var ids = new System.Collections.Generic.List<string>();
        foreach (var d in run.Diagnostics) ids.Add(d.Id);
        Assert.DoesNotContain("ZASAGA014", ids);
    }

    [Fact]
    public Task ZASAGA015_WithEfCoreStore_FiresIdempotencyHint()
    {
        var src = Header + """
            [Saga]
            public partial class IdemSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }

            // Pseudo-DI wiring that triggers the syntactic ZASAGA015 detection.
            // The generator does not bind these calls — name match is sufficient.
            public static class StartupShim
            {
                public static void Configure()
                {
                    Marker.WithEfCoreStore<object>();
                }
            }
            public static class Marker
            {
                public static void WithEfCoreStore<T>() { }
            }
            """;
        return GeneratorVerifier.ExpectAsync(src, "ZASAGA015");
    }

    [Fact]
    public async Task ZASAGA015_InMemoryBackend_DoesNotFire()
    {
        var src = Header + """
            [Saga]
            public partial class InMemorySaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        var run = await GeneratorVerifier.RunAsync(src);
        var ids = new System.Collections.Generic.List<string>();
        foreach (var d in run.Diagnostics) ids.Add(d.Id);
        Assert.DoesNotContain("ZASAGA015", ids);
    }
}
