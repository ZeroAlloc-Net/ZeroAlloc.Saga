using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Generator.Tests;

/// <summary>
/// Snapshot tests for <c>SagaCommandRegistryEmitter</c>. The emitter only runs
/// when the consumer compilation references <c>ZeroAlloc.Serialisation</c>
/// — detected by the generator via
/// <c>Compilation.GetTypeByMetadataName("ZeroAlloc.Serialisation.ZeroAllocSerializableAttribute")</c>.
///
/// Because <c>ZeroAlloc.Serialisation</c> is not part of this repo's
/// <c>Directory.Packages.props</c> (Option A/B in the task plan), these tests
/// take Option C: declare a stub <c>ZeroAllocSerializableAttribute</c> with the
/// exact metadata name inside the test source string when emission is desired.
/// The generator's <c>GetTypeByMetadataName</c> probe finds the stub, so the
/// registry is emitted. The negative test omits the stub and asserts the
/// registry source is absent from the generator output.
/// </summary>
public class SagaCommandRegistrySnapshotTests
{
    [Fact]
    public Task EmitsRegistry_WhenSerialisationReferenced()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            // Stub matching the metadata name probed by SagaGenerator. Treated by
            // the generator as evidence that ZeroAlloc.Serialisation is in scope.
            namespace ZeroAlloc.Serialisation
            {
                [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
                internal sealed class ZeroAllocSerializableAttribute : Attribute { }
            }

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record StockReserved(OrderId OrderId) : INotification;

            public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;
            public readonly record struct ChargeCmd(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class TwoStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation2(StockReserved e) => e.OrderId;
                [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 2)] public ChargeCmd Charge(StockReserved e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task DoesNotEmitRegistry_WhenSerialisationNotReferenced()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;

            public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class SingleStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }
}
