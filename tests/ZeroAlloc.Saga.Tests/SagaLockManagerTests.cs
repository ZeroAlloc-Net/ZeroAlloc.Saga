using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Tests;

/// <summary>
/// <see cref="SagaLockManager{TKey}"/> must serialize per key, run different keys in
/// parallel, and forget a key once nobody holds or waits for its lock. See #247.
/// </summary>
public class SagaLockManagerTests
{
    [Fact]
    public async Task Release_RemovesTheKey_WhenNobodyElseHoldsOrWaits()
    {
        var locks = new SagaLockManager<int>();

        for (var key = 0; key < 1_000; key++)
        {
            using var _ = await locks.AcquireAsync(key, CancellationToken.None);
            Assert.Equal(1, locks.TrackedKeyCount);
        }

        Assert.Equal(0, locks.TrackedKeyCount);
    }

    [Fact]
    public async Task Release_KeepsTheKey_WhileAWaiterIsQueued_ThenRemovesIt()
    {
        var locks = new SagaLockManager<int>();

        var first = await locks.AcquireAsync(1, CancellationToken.None);
        var second = locks.AcquireAsync(1, CancellationToken.None);
        Assert.False(second.IsCompleted);

        first.Dispose();
        Assert.Equal(1, locks.TrackedKeyCount);

        var secondLock = await second.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, locks.TrackedKeyCount);

        secondLock.Dispose();
        Assert.Equal(0, locks.TrackedKeyCount);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotAcquire_AndTheKeyIsRemovedAfterTheHolderReleases()
    {
        var locks = new SagaLockManager<int>();
        using var cts = new CancellationTokenSource();

        var holder = await locks.AcquireAsync(1, CancellationToken.None);
        var waiter = locks.AcquireAsync(1, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter.ConfigureAwait(false));
        Assert.Equal(1, locks.TrackedKeyCount);

        holder.Dispose();
        Assert.Equal(0, locks.TrackedKeyCount);

        // The lock is free again: a fresh acquire completes without waiting.
        var again = locks.AcquireAsync(1, CancellationToken.None);
        Assert.True(again.IsCompletedSuccessfully);
        (await again).Dispose();
        Assert.Equal(0, locks.TrackedKeyCount);
    }

    [Fact]
    public async Task AlreadyCancelledToken_DoesNotAcquire()
    {
        var locks = new SagaLockManager<int>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await locks.AcquireAsync(1, new CancellationToken(canceled: true)).ConfigureAwait(false));
        Assert.Equal(0, locks.TrackedKeyCount);
    }

    [Fact]
    public async Task DifferentKeys_DoNotBlockEachOther()
    {
        var locks = new SagaLockManager<int>();

        using var a = await locks.AcquireAsync(1, CancellationToken.None);
        var b = locks.AcquireAsync(2, CancellationToken.None);

        Assert.True(b.IsCompletedSuccessfully);
        (await b).Dispose();
    }

    /// <summary>
    /// Races acquire, release, removal and cancellation on a handful of hot keys. Every
    /// release that empties a key removes it, so new acquirers constantly race the removal;
    /// at no point may two holders own the same key, and no waiter may be stranded.
    /// </summary>
    [Fact]
    public async Task ConcurrentAcquireReleaseAndRemoval_NeverAdmitsTwoHoldersForOneKey()
    {
        const int Keys = 4;
        const int Workers = 32;
        const int IterationsPerWorker = 2_000;

        var locks = new SagaLockManager<int>();
        var inside = new int[Keys];
        var entered = new int[Keys];
        var overlaps = 0;
        var cancelled = 0;

        var workers = Enumerable.Range(0, Workers).Select(w => Task.Run(async () =>
        {
            var random = new Random(w);
            for (var i = 0; i < IterationsPerWorker; i++)
            {
                var key = random.Next(Keys);

                // Some waiters give up while queued, exercising the cancellation path.
                using var cts = random.Next(8) == 0
                    ? new CancellationTokenSource(TimeSpan.FromTicks(random.Next(1, 2_000)))
                    : null;

                IDisposable held;
                try
                {
                    held = await locks.AcquireAsync(key, cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    continue;
                }

                using (held)
                {
                    if (Interlocked.Increment(ref inside[key]) != 1)
                        Interlocked.Increment(ref overlaps);
                    Interlocked.Increment(ref entered[key]);

                    if (random.Next(4) == 0)
                        await Task.Yield();

                    Interlocked.Decrement(ref inside[key]);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, overlaps);
        Assert.Equal(Workers * IterationsPerWorker, entered.Sum() + cancelled);
        Assert.Equal(0, locks.TrackedKeyCount);
    }
}
