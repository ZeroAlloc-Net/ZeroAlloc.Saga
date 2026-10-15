using System;
using System.Linq;
using System.Threading.Tasks;
using ZeroAlloc.Saga.Tests.Fixtures;

namespace ZeroAlloc.Saga.Tests;

/// <summary>
/// The generated <c>Snapshot()</c> reuses one scratch writer per thread, #273. These tests check
/// that the reuse never leaks one snapshot into another: across threads, and on one thread
/// across states smaller and larger than the capacity the writer is kept at.
/// </summary>
public class SagaSnapshotBufferTests
{
    [Fact]
    public void Snapshots_of_alternating_large_and_small_states_on_one_thread_round_trip()
    {
        // 10 000 bytes is past the 4 096 B the writer is kept at, so it is dropped after the
        // large snapshot and a fresh one serves the small snapshot after it.
        foreach (var size in new[] { 3, 10_000, 3, 500, 10_000, 0, 4_000 })
        {
            var saga = Saga(size, seed: size);
            AssertRoundTrips(saga);
        }
    }

    [Fact]
    public async Task Concurrent_snapshots_on_many_threads_each_round_trip_their_own_state()
    {
        var workers = Enumerable.Range(0, Environment.ProcessorCount * 2).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                // Sizes vary per call, some past the retained capacity, so threads keep
                // swapping between a reused writer and a fresh one.
                var size = (worker * 7919 + i * 104_729) % 6_000;
                AssertRoundTrips(Saga(size, seed: worker * 1_000 + i));
            }
        }));

        await Task.WhenAll(workers);
    }

    private static PayloadSaga Saga(int size, int seed)
    {
        var payload = new byte[size];
        new Random(seed).NextBytes(payload);
        return new PayloadSaga
        {
            OrderId = new OrderId(seed),
            Note = new string((char)('a' + (seed % 26)), size % 300),
            Payload = payload,
        };
    }

    private static void AssertRoundTrips(PayloadSaga saga)
    {
        var snapshot = ((ISagaPersistableState)saga).Snapshot();

        var restored = new PayloadSaga();
        ((ISagaPersistableState)restored).Restore(snapshot);

        Assert.Equal(saga.OrderId, restored.OrderId);
        Assert.Equal(saga.Note, restored.Note);
        Assert.Equal(saga.Payload, restored.Payload);

        // A second snapshot of the same state is byte-identical, so nothing of an earlier,
        // longer snapshot was left in the reused writer.
        Assert.Equal(snapshot, ((ISagaPersistableState)saga).Snapshot());
    }
}
