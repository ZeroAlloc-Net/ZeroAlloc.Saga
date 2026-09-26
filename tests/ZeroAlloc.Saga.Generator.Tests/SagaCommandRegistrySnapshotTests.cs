using System;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;

using ZeroAlloc.TestHelpers;

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
    public void EmitsRegistry_WhenSerialisationReferenced()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void DoesNotEmitRegistry_WhenSerialisationNotReferenced()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    // A saga with a compensation command and a nested command type. Type.FullName writes the
    // nested type as Sample.Commands+ChargeCmd; the Roslyn display name is Sample.Commands.ChargeCmd.
    private const string CompensatingSagaWithNestedCommand = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;

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
        public readonly record struct CancelReserveCmd(OrderId OrderId) : IRequest<Unit>;

        public static class Commands
        {
            public readonly record struct ChargeCmd(OrderId OrderId) : IRequest<Unit>;
        }

        [Saga]
        public partial class NestedCommandSaga
        {
            public OrderId OrderId { get; set; }

            [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
            [CorrelationKey] public OrderId Correlation2(StockReserved e) => e.OrderId;

            [Step(Order = 1, Compensate = nameof(CancelReserve))]
            public ReserveCmd Reserve(OrderPlaced e) { OrderId = e.OrderId; return new(e.OrderId); }

            [Step(Order = 2)] public Commands.ChargeCmd Charge(StockReserved e) => new(e.OrderId);

            public CancelReserveCmd CancelReserve() => new(OrderId);
        }
        """;

    [Fact]
    public void Registry_Lists_Every_Step_And_Compensation_Command_As_Its_Type_FullName()
    {
        var registry = RegistrySource(GeneratorTestHost.Run(CompensatingSagaWithNestedCommand));

        Assert.Contains("typeof(global::Sample.CancelReserveCmd).FullName!,", registry, StringComparison.Ordinal);
        Assert.Contains("typeof(global::Sample.Commands.ChargeCmd).FullName!,", registry, StringComparison.Ordinal);
        Assert.Contains("typeof(global::Sample.ReserveCmd).FullName!,", registry, StringComparison.Ordinal);
        Assert.Contains(
            "internal static IReadOnlyList<string> GetTypeNames() => s_typeNames;", registry, StringComparison.Ordinal);
    }

    [Fact]
    public void Registry_Dispatches_By_The_Listed_Name_Not_By_A_Display_Name()
    {
        var registry = RegistrySource(GeneratorTestHost.Run(CompensatingSagaWithNestedCommand));

        // A string case label would hold the display name, which never matches a nested type's
        // Type.FullName, so a nested command could not be dispatched.
        Assert.DoesNotContain("case \"", registry, StringComparison.Ordinal);
        Assert.Contains(
            "if (string.Equals(typeName, s_typeNames[1], StringComparison.Ordinal))", registry, StringComparison.Ordinal);
        Assert.Contains(
            "ISerializer<global::Sample.Commands.ChargeCmd>", registry, StringComparison.Ordinal);
    }

    private static string RegistrySource(GeneratorDriver driver)
    {
        foreach (var result in driver.GetRunResult().Results)
        {
            foreach (var source in result.GeneratedSources)
            {
                if (string.Equals(source.HintName, "SagaCommandRegistry.g.cs", StringComparison.Ordinal))
                    return source.SourceText.ToString();
            }
        }

        throw new InvalidOperationException("SagaCommandRegistry.g.cs was not generated.");
    }
}
