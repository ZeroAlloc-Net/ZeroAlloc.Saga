using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox.Tests;

public class OutboxStoreSagaUnitOfWorkTests
{
    private sealed class CapturingOutboxStore : IOutboxStore
    {
        public string? CapturedTypeName;
        public byte[]? CapturedPayload;
        public int EnqueueDeferredCallCount;
        public int EnqueueAsyncCallCount;

        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
        {
            EnqueueAsyncCallCount++;
            CapturedTypeName = typeName;
            CapturedPayload = payload.ToArray();
            return default;
        }

        // Override the default-interface-method so we can verify the UoW prefers it
        // over the auto-commit fallback.
        public ValueTask EnqueueDeferredAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            EnqueueDeferredCallCount++;
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
    public async Task EnlistOutboxRowAsync_DelegatesToEnqueueDeferredAsync()
    {
        var store = new CapturingOutboxStore();
        var uow = new OutboxStoreSagaUnitOfWork(store);

        var payload = new byte[] { 1, 2, 3, 4 };
        await uow.EnlistOutboxRowAsync("Some.Cmd", payload, CancellationToken.None);

        Assert.Equal(1, store.EnqueueDeferredCallCount);
        Assert.Equal(0, store.EnqueueAsyncCallCount);
        Assert.Equal("Some.Cmd", store.CapturedTypeName);
        Assert.Equal(payload, store.CapturedPayload);
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new OutboxStoreSagaUnitOfWork(null!));
    }
}
