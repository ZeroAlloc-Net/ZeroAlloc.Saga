using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox.Tests;

public class OutboxSagaCommandPollerTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 5, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeOutboxStore : IOutboxStore
    {
        public List<OutboxEntry> Pending { get; } = new();
        public List<OutboxMessageId> Succeeded { get; } = new();
        public List<(OutboxMessageId Id, int RetryCount, DateTimeOffset NextRetryAt)> Failed { get; } = new();
        public List<(OutboxMessageId Id, string Error)> DeadLettered { get; } = new();

        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, DbTransaction? transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OutboxEntry>> FetchPendingAsync(int batchSize, CancellationToken ct)
        {
            // Snapshot then drain, mirroring the real store's "fetched" semantic — a second
            // PollOnce should see no pending entries unless re-added by the test.
            var snapshot = Pending.ToArray();
            Pending.Clear();
            return new ValueTask<IReadOnlyList<OutboxEntry>>(snapshot);
        }

        public ValueTask MarkSucceededAsync(OutboxMessageId id, CancellationToken ct)
        {
            Succeeded.Add(id);
            return default;
        }

        public ValueTask MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, CancellationToken ct)
        {
            Failed.Add((id, retryCount, nextRetryAt));
            return default;
        }

        public ValueTask DeadLetterAsync(OutboxMessageId id, string error, CancellationToken ct)
        {
            DeadLettered.Add((id, error));
            return default;
        }
    }

    private static OutboxEntry MakeEntry(string typeName, byte[] payload, int retryCount = 0, OutboxMessageId? id = null)
        => new()
        {
            Id = id ?? OutboxMessageId.New(),
            TypeName = typeName,
            RawPayload = payload,
            RetryCount = retryCount,
            CreatedAt = FixedTimestamp,
        };

    private static ServiceProvider BuildServices(FakeOutboxStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task PollOnceAsync_NoPending_DoesNothing()
    {
        var store = new FakeOutboxStore();
        await using var sp = BuildServices(store);
        SagaCommandRegistryDispatcher dispatch = (_, _, _, _) => default;

        var poller = new OutboxSagaCommandPoller(
            sp.GetRequiredService<IServiceScopeFactory>(),
            dispatch,
            NullLogger<OutboxSagaCommandPoller>.Instance);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Empty(store.Succeeded);
        Assert.Empty(store.Failed);
        Assert.Empty(store.DeadLettered);
    }

    [Fact]
    public async Task PollOnceAsync_DispatchSucceeds_MarksSucceeded()
    {
        var store = new FakeOutboxStore();
        var entry = MakeEntry("Sample.Cmd", new byte[] { 1, 2, 3 });
        store.Pending.Add(entry);

        var dispatchedTypes = new List<string>();
        SagaCommandRegistryDispatcher dispatch = (typeName, _, _, _) =>
        {
            dispatchedTypes.Add(typeName);
            return default;
        };

        await using var sp = BuildServices(store);
        var poller = new OutboxSagaCommandPoller(
            sp.GetRequiredService<IServiceScopeFactory>(),
            dispatch,
            NullLogger<OutboxSagaCommandPoller>.Instance);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "Sample.Cmd" }, dispatchedTypes);
        Assert.Single(store.Succeeded);
        Assert.Equal(entry.Id, store.Succeeded[0]);
        Assert.Empty(store.Failed);
        Assert.Empty(store.DeadLettered);
    }

    [Fact]
    public async Task PollOnceAsync_DispatchThrows_BelowMaxRetries_MarksFailed()
    {
        var store = new FakeOutboxStore();
        var entry = MakeEntry("Sample.Cmd", new byte[] { 9 }, retryCount: 1);
        store.Pending.Add(entry);

        SagaCommandRegistryDispatcher dispatch = (_, _, _, _) =>
            throw new InvalidOperationException("boom");

        await using var sp = BuildServices(store);
        var options = new OutboxSagaPollerOptions { MaxRetries = 5, RetryDelay = TimeSpan.FromMinutes(1) };
        var poller = new OutboxSagaCommandPoller(
            sp.GetRequiredService<IServiceScopeFactory>(),
            dispatch,
            NullLogger<OutboxSagaCommandPoller>.Instance,
            options);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Empty(store.Succeeded);
        Assert.Single(store.Failed);
        Assert.Equal(entry.Id, store.Failed[0].Id);
        Assert.Equal(2, store.Failed[0].RetryCount);
        Assert.Empty(store.DeadLettered);
    }

    [Fact]
    public async Task PollOnceAsync_DispatchThrows_AtMaxRetries_DeadLetters()
    {
        var store = new FakeOutboxStore();
        // retryCount=4 → next attempt would be 5 == MaxRetries → dead-letter.
        var entry = MakeEntry("Sample.Cmd", new byte[] { 9 }, retryCount: 4);
        store.Pending.Add(entry);

        SagaCommandRegistryDispatcher dispatch = (_, _, _, _) =>
            throw new InvalidOperationException("permanent");

        await using var sp = BuildServices(store);
        var options = new OutboxSagaPollerOptions { MaxRetries = 5 };
        var poller = new OutboxSagaCommandPoller(
            sp.GetRequiredService<IServiceScopeFactory>(),
            dispatch,
            NullLogger<OutboxSagaCommandPoller>.Instance,
            options);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Empty(store.Succeeded);
        Assert.Empty(store.Failed);
        Assert.Single(store.DeadLettered);
        Assert.Equal(entry.Id, store.DeadLettered[0].Id);
        Assert.Contains("permanent", store.DeadLettered[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollOnceAsync_MixedBatch_IsolatesFailures()
    {
        var store = new FakeOutboxStore();
        var ok1 = MakeEntry("Sample.Cmd", new byte[] { 1 });
        var bad = MakeEntry("Other.Cmd", new byte[] { 2 });
        var ok2 = MakeEntry("Sample.Cmd", new byte[] { 3 });
        store.Pending.Add(ok1);
        store.Pending.Add(bad);
        store.Pending.Add(ok2);

        SagaCommandRegistryDispatcher dispatch = (typeName, _, _, _) =>
        {
            if (string.Equals(typeName, "Other.Cmd", StringComparison.Ordinal))
                throw new InvalidOperationException("nope");
            return default;
        };

        await using var sp = BuildServices(store);
        var poller = new OutboxSagaCommandPoller(
            sp.GetRequiredService<IServiceScopeFactory>(),
            dispatch,
            NullLogger<OutboxSagaCommandPoller>.Instance);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, store.Succeeded.Count);
        Assert.Contains(ok1.Id, store.Succeeded);
        Assert.Contains(ok2.Id, store.Succeeded);
        Assert.Single(store.Failed);
        Assert.Equal(bad.Id, store.Failed[0].Id);
    }
}
