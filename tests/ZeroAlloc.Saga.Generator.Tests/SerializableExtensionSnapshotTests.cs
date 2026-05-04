using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Generator.Tests;

/// <summary>
/// Snapshot tests for <c>SerializableExtensionEmitter</c>. Auto-applies
/// <c>[ZeroAllocSerializable(SerializationFormat.Json)]</c> to step command types
/// via a partial-class extension when <c>ZeroAlloc.Serialisation</c> is referenced.
///
/// As with <see cref="SagaCommandRegistrySnapshotTests"/>, since
/// <c>ZeroAlloc.Serialisation</c> is not part of this repo's
/// <c>Directory.Packages.props</c>, the tests embed a stub
/// <c>ZeroAllocSerializableAttribute</c> + <c>SerializationFormat</c> inside the
/// compilation. The generator's <c>GetTypeByMetadataName</c> probe finds the stub.
/// </summary>
public class SerializableExtensionSnapshotTests
{
    [Fact]
    public Task EmitsZeroAllocSerializable_OnPartialRecordStruct()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace ZeroAlloc.Serialisation
            {
                public enum SerializationFormat { Json, MessagePack, MemoryPack }

                [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
                public sealed class ZeroAllocSerializableAttribute : Attribute
                {
                    public ZeroAllocSerializableAttribute() { }
                    public ZeroAllocSerializableAttribute(SerializationFormat format) { Format = format; }
                    public SerializationFormat Format { get; }
                }
            }

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public readonly partial record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class SingleStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task SkipsEmission_WhenUserAlreadyAppliedAttribute()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;
            using ZeroAlloc.Serialisation;

            namespace ZeroAlloc.Serialisation
            {
                public enum SerializationFormat { Json, MessagePack, MemoryPack }

                [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
                public sealed class ZeroAllocSerializableAttribute : Attribute
                {
                    public ZeroAllocSerializableAttribute() { }
                    public ZeroAllocSerializableAttribute(SerializationFormat format) { Format = format; }
                    public SerializationFormat Format { get; }
                }
            }

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;

            [ZeroAllocSerializable(SerializationFormat.MessagePack)]
            public readonly partial record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class SingleStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task SkipsEmission_WhenSerialisationNotReferenced()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public readonly partial record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;

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
