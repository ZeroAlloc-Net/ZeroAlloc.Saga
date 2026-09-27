using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga.Outbox.Redis.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Redis.Tests;

/// <summary>
/// RedisOutboxStore on the ZeroAlloc.Outbox 3.0 lease contract, against a real Redis. Every test
/// uses a key prefix of its own, so the tests share one container.
/// </summary>
public sealed class RedisOutboxStoreClaimTests(RedisFixture fx) : IClassFixture<RedisFixture>
{
    private static readonly TimeSpan LongLease = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ShortLease = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PastShortLease = TimeSpan.FromMilliseconds(1500);

    private readonly string _prefix = $"claim-{Guid.NewGuid():N}";

    private IDatabase Db => fx.Multiplexer.GetDatabase();
    private RedisKey PendingKey => $"{_prefix}:pending";
    private RedisKey SucceededKey => $"{_prefix}:succeeded";
    private RedisKey DeadLetterKey => $"{_prefix}:deadletter";
    private RedisKey EntryKey(OutboxMessageId id) => $"{_prefix}:entry:{id}";

    private RedisOutboxStore NewStore() => new(Db, new RedisOutboxOptions { KeyPrefix = _prefix });

    private static OutboxLease Lease(string hostId, TimeSpan duration) => new(hostId, duration);

    private static async Task<OutboxMessageId> EnqueueAndClaimAsync(RedisOutboxStore store, OutboxLease lease)
    {
        await store.EnqueueAsync("Test.Command", new byte[] { 1 }, transaction: null, default).ConfigureAwait(false);
        var claimed = await store.ClaimPendingAsync(10, lease, default).ConfigureAwait(false);
        Assert.Equal(["Test.Command"], claimed.Select(e => e.TypeName), StringComparer.Ordinal);
        return claimed[0].Id;
    }

    [Fact]
    public async Task Concurrent_Claimers_Claim_Each_Command_Exactly_Once()
    {
        const int commands = 200;
        const int claimers = 8;
        var writer = NewStore();
        for (var i = 0; i < commands; i++)
            await writer.EnqueueAsync("Test.Command", BitConverter.GetBytes(i), transaction: null, default);

        var claimed = new ConcurrentBag<int>();
        var tasks = new Task[claimers];
        for (var c = 0; c < claimers; c++)
        {
            var store = NewStore();
            var lease = Lease($"host-{c}", LongLease);
            tasks[c] = Task.Run(async () =>
            {
                while (true)
                {
                    var batch = await store.ClaimPendingAsync(7, lease, default).ConfigureAwait(false);
                    // More claims than commands proves a duplicate; stop so the asserts report it
                    // instead of spinning on entries a broken claim keeps handing out.
                    if (batch.Count == 0 || claimed.Count > commands) return;
                    foreach (var entry in batch)
                        claimed.Add(BitConverter.ToInt32(entry.RawPayload, 0));
                }
            });
        }
        await Task.WhenAll(tasks);

        Assert.Equal(commands, claimed.Count);
        Assert.Equal(commands, claimed.Distinct().Count());
    }

    [Fact]
    public async Task Claim_Returns_The_Entry_And_Leases_It()
    {
        var store = NewStore();
        await store.EnqueueAsync("Test.Command", new byte[] { 4, 2 }, transaction: null, default);

        var claimed = await store.ClaimPendingAsync(10, Lease("host-a", LongLease), default);

        Assert.Equal(["Test.Command"], claimed.Select(e => e.TypeName), StringComparer.Ordinal);
        Assert.Equal(new byte[] { 4, 2 }, claimed[0].RawPayload);
        Assert.Equal(0, claimed[0].RetryCount);
        Assert.Equal("host-a", (string?)await Db.HashGetAsync(EntryKey(claimed[0].Id), "lockedBy"));
        Assert.Empty(await store.ClaimPendingAsync(10, Lease("host-b", LongLease), default));
    }

    [Fact]
    public async Task Expired_Lease_Is_Reclaimed_By_Another_Host()
    {
        var store = NewStore();
        var id = await EnqueueAndClaimAsync(store, Lease("host-a", ShortLease));
        var hostB = Lease("host-b", LongLease);
        Assert.Empty(await store.ClaimPendingAsync(10, hostB, default));

        await Task.Delay(PastShortLease);
        var reclaimed = await store.ClaimPendingAsync(10, hostB, default);

        Assert.Equal([id], reclaimed.Select(e => e.Id));
        Assert.Equal("host-b", (string?)await Db.HashGetAsync(EntryKey(id), "lockedBy"));
    }

    [Fact]
    public async Task Renew_Extends_A_Held_Lease_And_Fails_After_Another_Host_Claimed()
    {
        var store = NewStore();
        var hostA = Lease("host-a", ShortLease);
        var id = await EnqueueAndClaimAsync(store, hostA);
        Assert.True(await store.RenewLeaseAsync(id, hostA, default));

        await Task.Delay(PastShortLease);
        var hostB = Lease("host-b", LongLease);
        Assert.Equal([id], (await store.ClaimPendingAsync(10, hostB, default)).Select(e => e.Id));

        Assert.False(await store.RenewLeaseAsync(id, hostA, default));
        Assert.True(await store.RenewLeaseAsync(id, hostB, default));
    }

    [Fact]
    public async Task Renew_Fails_Once_The_Lease_Has_Expired_Even_Without_A_Takeover()
    {
        var store = NewStore();
        var hostA = Lease("host-a", ShortLease);
        var id = await EnqueueAndClaimAsync(store, hostA);

        await Task.Delay(PastShortLease);

        Assert.False(await store.RenewLeaseAsync(id, hostA, default));
    }

    [Fact]
    public async Task Late_Mark_Returns_False_And_Does_Not_Overwrite_The_Result()
    {
        var store = NewStore();
        var hostA = Lease("host-a", ShortLease);
        var id = await EnqueueAndClaimAsync(store, hostA);
        await Task.Delay(PastShortLease);
        var hostB = Lease("host-b", LongLease);
        Assert.Equal([id], (await store.ClaimPendingAsync(10, hostB, default)).Select(e => e.Id));

        Assert.False(await store.MarkSucceededAsync(id, hostA, default));
        Assert.True(await store.MarkSucceededAsync(id, hostB, default));
        Assert.False(await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, hostA, default));
        Assert.False(await store.DeadLetterAsync(id, "late", hostA, default));
        Assert.False(await store.MarkSucceededAsync(id, hostB, default));

        Assert.Equal("Succeeded", (string?)await Db.HashGetAsync(EntryKey(id), "status"));
        Assert.False(await Db.HashExistsAsync(EntryKey(id), "lockedBy"));
        Assert.True(await Db.SetContainsAsync(SucceededKey, id.ToString()));
        Assert.False(await Db.SetContainsAsync(DeadLetterKey, id.ToString()));
        Assert.Null(await Db.SortedSetScoreAsync(PendingKey, id.ToString()));
    }

    [Fact]
    public async Task Mark_Without_A_Claim_Returns_False()
    {
        var store = NewStore();
        await store.EnqueueAsync("Test.Command", new byte[] { 1 }, transaction: null, default);
        Assert.Equal(1, await Db.SortedSetLengthAsync(PendingKey));
        var members = await Db.SortedSetRangeByRankAsync(PendingKey);
        Assert.True(OutboxMessageId.TryParse((string?)members[0], null, out var id));

        Assert.False(await store.MarkSucceededAsync(id, Lease("host-a", LongLease), default));
        Assert.Equal("Pending", (string?)await Db.HashGetAsync(EntryKey(id), "status"));
    }

    [Fact]
    public async Task MarkFailed_Reschedules_Clears_The_Lease_And_Keeps_It_Pending()
    {
        var store = NewStore();
        var lease = Lease("host-a", LongLease);
        var id = await EnqueueAndClaimAsync(store, lease);
        var later = DateTimeOffset.UtcNow.AddHours(1);

        Assert.True(await store.MarkFailedAsync(id, 1, later, lease, default));

        Assert.Equal("Pending", (string?)await Db.HashGetAsync(EntryKey(id), "status"));
        Assert.Equal(1, (int)await Db.HashGetAsync(EntryKey(id), "retryCount"));
        Assert.False(await Db.HashExistsAsync(EntryKey(id), "lockedBy"));
        Assert.Equal(later.ToUnixTimeMilliseconds(), (long)(await Db.SortedSetScoreAsync(PendingKey, id.ToString()))!.Value);
        Assert.Empty(await store.ClaimPendingAsync(10, lease, default));
    }

    [Fact]
    public async Task MarkFailed_Entry_Is_Claimable_Again_When_Due()
    {
        var store = NewStore();
        var lease = Lease("host-a", LongLease);
        var id = await EnqueueAndClaimAsync(store, lease);

        Assert.True(await store.MarkFailedAsync(id, 1, DateTimeOffset.UtcNow, lease, default));
        var again = await store.ClaimPendingAsync(10, Lease("host-b", LongLease), default);

        Assert.Equal([id], again.Select(e => e.Id));
        Assert.Equal(1, again[0].RetryCount);
    }

    [Fact]
    public async Task DeadLetter_Moves_The_Entry_To_The_DeadLetter_Set()
    {
        var store = NewStore();
        var lease = Lease("host-a", LongLease);
        var id = await EnqueueAndClaimAsync(store, lease);

        Assert.True(await store.DeadLetterAsync(id, "boom", lease, default));

        Assert.Equal("DeadLetter", (string?)await Db.HashGetAsync(EntryKey(id), "status"));
        Assert.Equal("boom", (string?)await Db.HashGetAsync(EntryKey(id), "error"));
        Assert.True(await Db.SetContainsAsync(DeadLetterKey, id.ToString()));
        Assert.Null(await Db.SortedSetScoreAsync(PendingKey, id.ToString()));
    }

    [Fact]
    public async Task Release_Makes_An_Entry_Immediately_Claimable()
    {
        var store = NewStore();
        var hostA = Lease("host-a", LongLease);
        var hostB = Lease("host-b", LongLease);
        var id = await EnqueueAndClaimAsync(store, hostA);
        Assert.Empty(await store.ClaimPendingAsync(10, hostB, default));

        Assert.Equal(0, await store.ReleaseLeasesAsync([id], hostB, default));
        Assert.Equal(1, await store.ReleaseLeasesAsync([id], hostA, default));
        var claimed = await store.ClaimPendingAsync(10, hostB, default);

        Assert.Equal([id], claimed.Select(e => e.Id));
    }

    [Fact]
    public async Task A_3x_Failed_Entry_Is_Claimed_After_The_Upgrade()
    {
        // Saga 3.x marked a retried entry status=Failed and left it in the pending set, with no
        // lease fields. The claim must treat it as pending and rewrite the status.
        var id = OutboxMessageId.New();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        await Db.HashSetAsync(EntryKey(id),
        [
            new HashEntry("typeName", "Legacy.Command"),
            new HashEntry("payload", new byte[] { 7 }),
            new HashEntry("retryCount", 2),
            new HashEntry("status", "Failed"),
            new HashEntry("createdAt", past),
            new HashEntry("nextRetryAt", past),
        ]);
        await Db.SortedSetAddAsync(PendingKey, id.ToString(), past);
        var store = NewStore();
        var lease = Lease("host-a", LongLease);

        var claimed = await store.ClaimPendingAsync(10, lease, default);

        Assert.Equal([id], claimed.Select(e => e.Id));
        Assert.Equal("Legacy.Command", claimed[0].TypeName);
        Assert.Equal(2, claimed[0].RetryCount);
        Assert.Equal("Pending", (string?)await Db.HashGetAsync(EntryKey(id), "status"));
        Assert.True(await store.MarkSucceededAsync(id, lease, default));
    }

    [Fact]
    public async Task Claim_Removes_Ids_Whose_Entry_Is_Missing_Or_No_Longer_Pending()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        var missing = OutboxMessageId.New();
        await Db.SortedSetAddAsync(PendingKey, missing.ToString(), past);
        var finished = OutboxMessageId.New();
        await Db.HashSetAsync(EntryKey(finished),
        [
            new HashEntry("typeName", "Done.Command"),
            new HashEntry("payload", new byte[] { 1 }),
            new HashEntry("status", "Succeeded"),
        ]);
        await Db.SortedSetAddAsync(PendingKey, finished.ToString(), past);

        var claimed = await NewStore().ClaimPendingAsync(10, Lease("host-a", LongLease), default);

        Assert.Empty(claimed);
        Assert.Equal(0, await Db.SortedSetLengthAsync(PendingKey));
    }

    private const string DbPrefix = "ns:";

    // StackExchange.Redis prefixes KEYS through a key-prefixed database, but never ARGV.
    private RedisOutboxStore NewPrefixedStore()
        => new(Db.WithKeyPrefix(DbPrefix), new RedisOutboxOptions { KeyPrefix = _prefix });

    private RedisKey RawEntryKey(OutboxMessageId id) => $"{DbPrefix}{_prefix}:entry:{id}";
    private RedisKey RawPendingKey => $"{DbPrefix}{_prefix}:pending";

    [Fact]
    public async Task Claim_And_Marks_Work_Through_A_Key_Prefixed_Database()
    {
        var store = NewPrefixedStore();
        var lease = Lease("host-a", LongLease);
        await store.EnqueueAsync("Test.Command", new byte[] { 3 }, transaction: null, default);

        var claimed = await store.ClaimPendingAsync(10, lease, default);

        Assert.Equal(["Test.Command"], claimed.Select(e => e.TypeName), StringComparer.Ordinal);
        var id = claimed[0].Id;
        Assert.Equal(new byte[] { 3 }, claimed[0].RawPayload);
        Assert.Equal("host-a", (string?)await Db.HashGetAsync(RawEntryKey(id), "lockedBy"));
        Assert.NotNull(await Db.SortedSetScoreAsync(RawPendingKey, id.ToString()));
        Assert.True(await store.RenewLeaseAsync(id, lease, default));
        Assert.True(await store.MarkSucceededAsync(id, lease, default));
        Assert.Equal("Succeeded", (string?)await Db.HashGetAsync(RawEntryKey(id), "status"));
        Assert.True(await Db.SetContainsAsync($"{DbPrefix}{_prefix}:succeeded", id.ToString()));
    }

    [Fact]
    public async Task Release_Works_Through_A_Key_Prefixed_Database()
    {
        // Seeded directly, so this test does not depend on the claim.
        var id = OutboxMessageId.New();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var until = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await Db.HashSetAsync(RawEntryKey(id),
        [
            new HashEntry("typeName", "Test.Command"),
            new HashEntry("payload", new byte[] { 1 }),
            new HashEntry("retryCount", 0),
            new HashEntry("status", "Pending"),
            new HashEntry("createdAt", now),
            new HashEntry("lockedBy", "host-a"),
            new HashEntry("lockedUntil", until),
        ]);
        await Db.SortedSetAddAsync(RawPendingKey, id.ToString(), until);

        Assert.Equal(1, await NewPrefixedStore().ReleaseLeasesAsync([id], Lease("host-a", LongLease), default));

        Assert.False(await Db.HashExistsAsync(RawEntryKey(id), "lockedBy"));
        Assert.True((await Db.SortedSetScoreAsync(RawPendingKey, id.ToString()))!.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9_999)]
    public async Task A_Lease_Shorter_Than_One_Millisecond_Is_Rejected(long ticks)
    {
        var store = NewStore();
        var lease = Lease("host-a", TimeSpan.FromTicks(ticks));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.ClaimPendingAsync(10, lease, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.RenewLeaseAsync(OutboxMessageId.New(), lease, default));
    }

    [Fact]
    public async Task Corrupt_Numeric_Fields_Are_Read_As_Zero_And_Do_Not_Stall_The_Batch()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        var corrupt = OutboxMessageId.New();
        await Db.HashSetAsync(EntryKey(corrupt),
        [
            new HashEntry("typeName", "Corrupt.Command"),
            new HashEntry("payload", new byte[] { 9 }),
            new HashEntry("retryCount", "not-a-number"),
            new HashEntry("status", "Pending"),
            new HashEntry("createdAt", "garbage"),
        ]);
        await Db.SortedSetAddAsync(PendingKey, corrupt.ToString(), past);
        var store = NewStore();
        await store.EnqueueAsync("Test.Command", new byte[] { 1 }, transaction: null, default);

        var claimed = await store.ClaimPendingAsync(10, Lease("host-a", LongLease), default);

        Assert.Equal(["Corrupt.Command", "Test.Command"], claimed.Select(e => e.TypeName), StringComparer.Ordinal);
        Assert.Equal(0, claimed[0].RetryCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, claimed[0].CreatedAt);
    }

    [Fact]
    public async Task Mark_After_The_Lease_Expired_Without_A_Takeover_Succeeds()
    {
        var store = NewStore();
        var hostA = Lease("host-a", ShortLease);
        var id = await EnqueueAndClaimAsync(store, hostA);

        await Task.Delay(PastShortLease);

        Assert.True(await store.MarkSucceededAsync(id, hostA, default));
        Assert.Equal("Succeeded", (string?)await Db.HashGetAsync(EntryKey(id), "status"));
    }

    [Fact]
    public async Task A_3x_Failed_Entry_Scored_In_The_Future_Is_Not_Claimed()
    {
        var id = OutboxMessageId.New();
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        await Db.HashSetAsync(EntryKey(id),
        [
            new HashEntry("typeName", "Legacy.Command"),
            new HashEntry("payload", new byte[] { 7 }),
            new HashEntry("retryCount", 1),
            new HashEntry("status", "Failed"),
            new HashEntry("createdAt", past),
            new HashEntry("nextRetryAt", future),
        ]);
        await Db.SortedSetAddAsync(PendingKey, id.ToString(), future);

        Assert.Empty(await NewStore().ClaimPendingAsync(10, Lease("host-a", LongLease), default));

        Assert.Equal("Failed", (string?)await Db.HashGetAsync(EntryKey(id), "status"));
        Assert.Equal(future, (long)(await Db.SortedSetScoreAsync(PendingKey, id.ToString()))!.Value);
    }
}
