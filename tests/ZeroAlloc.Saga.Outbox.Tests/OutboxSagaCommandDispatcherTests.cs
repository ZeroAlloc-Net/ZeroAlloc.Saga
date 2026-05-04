using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox.Tests;

public class OutboxSagaCommandDispatcherTests
{
    public readonly record struct DispatchableCmd(int X) : IRequest<Unit>;

    // Mediator's ZAM001 analyzer requires a registered handler for every IRequest<T>
    // declared in the project. This handler is never invoked.
    public sealed class DispatchableCmdHandler : IRequestHandler<DispatchableCmd, Unit>
    {
        public ValueTask<Unit> Handle(DispatchableCmd request, CancellationToken cancellationToken)
            => new(Unit.Value);
    }

    private sealed class FakeSerializer : ISerializer<DispatchableCmd>
    {
        public void Serialize(IBufferWriter<byte> writer, DispatchableCmd value)
        {
            var span = writer.GetSpan(4);
            BinaryPrimitives.WriteInt32LittleEndian(span, value.X);
            writer.Advance(4);
        }

        public DispatchableCmd Deserialize(ReadOnlySpan<byte> buffer)
            => new(BinaryPrimitives.ReadInt32LittleEndian(buffer));
    }

    private sealed class CapturingOutboxStore : IOutboxStore
    {
        public string? CapturedTypeName;
        public byte[]? CapturedPayload;
        public int CallCount;

        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
        {
            CallCount++;
            CapturedTypeName = typeName;
            CapturedPayload = payload.ToArray();
            return default;
        }

        public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task DispatchAsync_SerializesAndEnqueues()
    {
        var store = new CapturingOutboxStore();
        var services = new ServiceCollection();
        services.AddSingleton<ISerializer<DispatchableCmd>>(new FakeSerializer());
        using var sp = services.BuildServiceProvider();

        var dispatcher = new OutboxSagaCommandDispatcher(store, sp);
        await dispatcher.DispatchAsync(new DispatchableCmd(42), CancellationToken.None);

        Assert.Equal(1, store.CallCount);
        Assert.NotNull(store.CapturedTypeName);
        Assert.Contains("DispatchableCmd", store.CapturedTypeName!, StringComparison.Ordinal);
        Assert.NotNull(store.CapturedPayload);
        Assert.Equal(4, store.CapturedPayload!.Length);
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(store.CapturedPayload));
    }

    [Fact]
    public async Task DispatchAsync_TypeName_IsFullyQualified()
    {
        var store = new CapturingOutboxStore();
        var services = new ServiceCollection();
        services.AddSingleton<ISerializer<DispatchableCmd>>(new FakeSerializer());
        using var sp = services.BuildServiceProvider();

        var dispatcher = new OutboxSagaCommandDispatcher(store, sp);
        await dispatcher.DispatchAsync(new DispatchableCmd(7), CancellationToken.None);

        // Matches the case label written by the generator-emitted SagaCommandRegistry.
        Assert.Equal(typeof(DispatchableCmd).FullName, store.CapturedTypeName);
    }

    [Fact]
    public async Task DispatchAsync_MissingSerializer_Throws()
    {
        var store = new CapturingOutboxStore();
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var dispatcher = new OutboxSagaCommandDispatcher(store, sp);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.DispatchAsync(new DispatchableCmd(1), CancellationToken.None).ConfigureAwait(false));
        Assert.Equal(0, store.CallCount);
    }
}
