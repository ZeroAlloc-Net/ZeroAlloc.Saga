using System;
using System.Linq;
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
    }

    [Fact]
    public void Source_Lists_Every_Command_Type_And_Forwards_Serialized_Dispatch_To_The_Registry()
    {
        // WithOutbox() takes its type names from the source's CommandTypes, as Type.FullName, and
        // dispatches through DispatchSerializedAsync. The registry is referenced directly, so it
        // needs no reflection and no trimming root, #176.
        var source = GeneratedSource(GeneratorTestHost.Run(CompensatingSagaWithNestedCommand), "GeneratedSagaCommandSource.g.cs");

        Assert.Contains("typeof(global::Sample.CancelReserveCmd),", source, StringComparison.Ordinal);
        Assert.Contains("typeof(global::Sample.Commands.ChargeCmd),", source, StringComparison.Ordinal);
        Assert.Contains("typeof(global::Sample.ReserveCmd),", source, StringComparison.Ordinal);
        Assert.Contains("public override bool CanDispatchSerialized => true;", source, StringComparison.Ordinal);
        Assert.Contains(
            "=> SagaCommandRegistry.DispatchAsync(typeName, payload, services, services.GetRequiredService<IMediator>(), ct);",
            source,
            StringComparison.Ordinal);
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

    // Struct step and compensation commands next to a class command, with stubs for what
    // ZeroAlloc.Serialisation declares and the Mediator generator emits in a real consumer,
    // so the whole generated output compiles.
    private const string StructAndClassCommandSaga = """
        using System;
        using System.Buffers;
        using System.Threading;
        using System.Threading.Tasks;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;

        namespace ZeroAlloc.Serialisation
        {
            public enum SerializationFormat { SystemTextJson, MessagePack, MemoryPack }

            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
            internal sealed class ZeroAllocSerializableAttribute : Attribute
            {
                public ZeroAllocSerializableAttribute(SerializationFormat format) { }
            }

            public interface ISerializer<T>
            {
                void Serialize(IBufferWriter<byte> writer, T value);
                T? Deserialize(ReadOnlySpan<byte> buffer);
            }
        }

        namespace ZeroAlloc.Mediator
        {
            internal interface IMediator
            {
                ValueTask<Unit> Send(Sample.ReserveCmd request, CancellationToken ct);
                ValueTask<Unit> Send(Sample.CancelReserveCmd request, CancellationToken ct);
                ValueTask<Unit> Send(Sample.ChargeCmd request, CancellationToken ct);
            }

            internal static class MediatorServiceCollectionExtensions
            {
                public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddMediator(
                    this Microsoft.Extensions.DependencyInjection.IServiceCollection services) => services;
            }
        }

        namespace Sample
        {
            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record StockReserved(OrderId OrderId) : INotification;

            // Partial, so the generator also adds its [ZeroAllocSerializable] declaration to a struct.
            public readonly partial record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;
            public readonly record struct CancelReserveCmd(OrderId OrderId) : IRequest<Unit>;
            public sealed record ChargeCmd(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class MixedCommandSaga
            {
                public OrderId OrderId { get; set; }

                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation2(StockReserved e) => e.OrderId;

                [Step(Order = 1, Compensate = nameof(CancelReserve))]
                public ReserveCmd Reserve(OrderPlaced e) { OrderId = e.OrderId; return new(e.OrderId); }

                [Step(Order = 2)] public ChargeCmd Charge(StockReserved e) => new(e.OrderId);

                public CancelReserveCmd CancelReserve() => new(OrderId);
            }
        }
        """;

    [Fact]
    public void Generated_Code_Compiles_For_Struct_Step_And_Compensation_Commands()
    {
        // #202: the registry null-checked every deserialized command, and a struct cannot be
        // compared to null, so a struct command failed the build with CS0037.
        var compilation = GeneratorTestHost.RunAndCompile(StructAndClassCommandSaga);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Registry_Null_Checks_Only_Reference_Type_Commands()
    {
        var registry = RegistrySource(GeneratorTestHost.Run(StructAndClassCommandSaga));

        // ISerializer<T>.Deserialize returns T?, which can be null only for a reference type.
        Assert.Contains("returned null for Sample.ChargeCmd.", registry, StringComparison.Ordinal);
        Assert.DoesNotContain("returned null for Sample.ReserveCmd.", registry, StringComparison.Ordinal);
        Assert.DoesNotContain("returned null for Sample.CancelReserveCmd.", registry, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(registry, "if (cmd is null)"));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string RegistrySource(GeneratorDriver driver) => GeneratedSource(driver, "SagaCommandRegistry.g.cs");

    private static string GeneratedSource(GeneratorDriver driver, string hintName)
    {
        foreach (var result in driver.GetRunResult().Results)
        {
            foreach (var source in result.GeneratedSources)
            {
                if (string.Equals(source.HintName, hintName, StringComparison.Ordinal))
                    return source.SourceText.ToString();
            }
        }

        throw new InvalidOperationException($"{hintName} was not generated.");
    }
}
