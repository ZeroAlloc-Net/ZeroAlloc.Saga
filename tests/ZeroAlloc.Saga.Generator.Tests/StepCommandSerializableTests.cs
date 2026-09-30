using System;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Saga.Generator.Tests;

/// <summary>
/// The Saga generator must not attach <c>[ZeroAllocSerializable]</c> to step command types.
/// Roslyn runs every source generator against the same input compilation, so
/// ZeroAlloc.Serialisation's generator never sees an attribute another generator adds and emits
/// no <c>ISerializer&lt;T&gt;</c> for it (#207). The user applies the attribute or registers
/// a serializer; the Saga generator stays out of it.
/// </summary>
public class StepCommandSerializableTests
{
    private const string SerialisationStub = """
        namespace ZeroAlloc.Serialisation
        {
            public enum SerializationFormat { SystemTextJson, MessagePack, MemoryPack }

            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
            public sealed class ZeroAllocSerializableAttribute : System.Attribute
            {
                public ZeroAllocSerializableAttribute() { }
                public ZeroAllocSerializableAttribute(SerializationFormat format) { Format = format; }
                public SerializationFormat Format { get; }
            }
        }

        """;

    [Theory]
    [InlineData("public readonly partial record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;")]
    [InlineData("public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;")]
    public void Emits_No_Partial_Declaration_Of_A_Step_Command(string commandDeclaration)
    {
        var src = SerialisationStub + $$"""
            namespace Sample
            {
                using System;
                using ZeroAlloc.Mediator;
                using ZeroAlloc.Saga;

                public readonly record struct OrderId(int V) : IEquatable<OrderId>;
                public sealed record OrderPlaced(OrderId OrderId) : INotification;
                {{commandDeclaration}}

                [Saga]
                public partial class SingleStepSaga
                {
                    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                    [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
                }
            }
            """;

        var result = GeneratorTestHost.Run(src).GetRunResult();

        Assert.NotEmpty(result.GeneratedTrees);
        Assert.DoesNotContain(result.GeneratedTrees,
            t => t.ToString().Contains("[ZeroAllocSerializable", StringComparison.Ordinal));
        Assert.DoesNotContain(result.GeneratedTrees,
            t => t.ToString().Contains("partial record struct ReserveCmd", StringComparison.Ordinal));
        Assert.Empty(result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }
}
