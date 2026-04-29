using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Saga.EfCore.Tests.Fixtures;

namespace ZeroAlloc.Saga.EfCore.Tests;

/// <summary>
/// Forward-path tests for <see cref="EfCoreSagaStore{TSaga,TKey}"/> exercising
/// load/save/restore round-trips against a SQLite in-memory database. Each
/// test owns its own <see cref="SqliteFixture"/> so the database is created
/// and disposed deterministically.
/// </summary>
public sealed class ForwardPathTests
{
    private static EfCoreSagaStore<OrderFulfillmentSaga, OrderId> CreateStore(DbContext ctx)
        => new(ctx, NullLogger<EfCoreSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);

    [Fact]
    public async Task Save_PersistsState_RestorableOnLoad()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();

        var orderId = new OrderId(42);

        // Save into one DbContext...
        var ctx1 = fx.CreateContext();
        await using (ctx1.ConfigureAwait(false))
        {
            var store = CreateStore(ctx1);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            // Drive into Step1: state fields populated from event.
            Assert.True(saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced));
            saga.ReserveStock(new OrderPlaced(orderId, 199.99m));
            await store.SaveAsync(orderId, saga, default);
        }

        // ...and load through a fresh context.
        var ctx2 = fx.CreateContext();
        await using (ctx2.ConfigureAwait(false))
        {
            var store2 = CreateStore(ctx2);
            var loaded = await store2.TryLoadAsync(orderId, default);
            Assert.NotNull(loaded);
            Assert.Equal(orderId, loaded!.OrderId);
            Assert.Equal(199.99m, loaded.Total);
        }
    }

    [Fact]
    public async Task Save_PersistsFsmState_RestoresCurrentState()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(1);

        var ctx1 = fx.CreateContext();
        await using (ctx1.ConfigureAwait(false))
        {
            var store = CreateStore(ctx1);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            saga.ReserveStock(new OrderPlaced(orderId, 10m));
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
            saga.ChargeCustomer(new StockReserved(orderId));
            await store.SaveAsync(orderId, saga, default);
            Assert.Equal(OrderFulfillmentSagaFsm.State.Step2, saga.Fsm.Current);
        }

        var ctx2 = fx.CreateContext();
        await using (ctx2.ConfigureAwait(false))
        {
            var store = CreateStore(ctx2);
            var loaded = await store.TryLoadAsync(orderId, default);
            Assert.NotNull(loaded);
            Assert.Equal(OrderFulfillmentSagaFsm.State.Step2, loaded!.Fsm.Current);
        }
    }

    [Fact]
    public async Task Restore_VersionMismatch_ThrowsSagaStateVersionMismatchException()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(7);

        // Persist a normal saga first.
        var ctx1 = fx.CreateContext();
        await using (ctx1.ConfigureAwait(false))
        {
            var store = CreateStore(ctx1);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            saga.ReserveStock(new OrderPlaced(orderId, 1m));
            await store.SaveAsync(orderId, saga, default);
        }

        // Corrupt the version byte directly in the DB. Reassign the State
        // array (rather than mutating in-place) so EF's change tracker
        // notices the property has changed and emits an UPDATE.
        var ctx2 = fx.CreateContext();
        await using (ctx2.ConfigureAwait(false))
        {
            var entity = await ctx2.Set<SagaInstanceEntity>()
                .AsTracking()
                .FirstAsync(e => e.CorrelationKey == orderId.ToString());
            var corrupted = new byte[entity.State.Length];
            entity.State.CopyTo(corrupted, 0);
            corrupted[0] = 99; // bogus version byte
            entity.State = corrupted;
            await ctx2.SaveChangesAsync();
        }

        // Now load — should throw.
        var ctx3 = fx.CreateContext();
        await using (ctx3.ConfigureAwait(false))
        {
            var store = CreateStore(ctx3);
            await Assert.ThrowsAsync<SagaStateVersionMismatchException>(
                async () => await store.TryLoadAsync(orderId, default).ConfigureAwait(false));
        }
    }

    [Fact]
    public async Task Save_NewSaga_InsertsRow()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(11);

        var ctx1 = fx.CreateContext();
        await using (ctx1.ConfigureAwait(false))
        {
            var store = CreateStore(ctx1);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            saga.ReserveStock(new OrderPlaced(orderId, 5m));
            await store.SaveAsync(orderId, saga, default);
        }

        var ctx2 = fx.CreateContext();
        await using (ctx2.ConfigureAwait(false))
        {
            var rows = await ctx2.Set<SagaInstanceEntity>()
                .AsNoTracking()
                .Where(e => e.CorrelationKey == orderId.ToString())
                .ToListAsync();
            Assert.Single(rows);
            Assert.Contains("OrderFulfillmentSaga", rows[0].SagaType, StringComparison.Ordinal);
            Assert.Equal(orderId.ToString(), rows[0].CorrelationKey);
            Assert.Equal(OrderFulfillmentSagaFsm.State.Step1.ToString(), rows[0].CurrentFsmState);
        }
    }

    [Fact]
    public async Task Save_ExistingSaga_UpdatesRow_AndIncrementsRowVersion()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(99);

        byte[] initialRowVersion;
        DateTimeOffset initialUpdatedAt;
        var ctx1 = fx.CreateContext();
        await using (ctx1.ConfigureAwait(false))
        {
            var store = CreateStore(ctx1);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            saga.ReserveStock(new OrderPlaced(orderId, 1m));
            await store.SaveAsync(orderId, saga, default);

            var entity = await ctx1.Set<SagaInstanceEntity>().AsNoTracking().FirstAsync();
            initialRowVersion = entity.RowVersion;
            initialUpdatedAt = entity.UpdatedAt;
        }

        // Sleep a touch so UpdatedAt differs reliably across coarse-grained clocks.
        await Task.Delay(5);

        var ctx2 = fx.CreateContext();
        await using (ctx2.ConfigureAwait(false))
        {
            var store = CreateStore(ctx2);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
            saga.ChargeCustomer(new StockReserved(orderId));
            await store.SaveAsync(orderId, saga, default);
        }

        var ctx3 = fx.CreateContext();
        await using (ctx3.ConfigureAwait(false))
        {
            var entity = await ctx3.Set<SagaInstanceEntity>().AsNoTracking().FirstAsync();
            Assert.Equal(OrderFulfillmentSagaFsm.State.Step2.ToString(), entity.CurrentFsmState);
            Assert.True(entity.UpdatedAt > initialUpdatedAt, "UpdatedAt should advance");
            Assert.False(initialRowVersion.AsSpan().SequenceEqual(entity.RowVersion),
                "RowVersion should change after update");
        }
    }

    [Fact]
    public async Task Remove_Terminal_RemovesRow()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(123);

        var ctx1 = fx.CreateContext();
        await using (ctx1.ConfigureAwait(false))
        {
            var store = CreateStore(ctx1);
            var saga = await store.LoadOrCreateAsync(orderId, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            saga.ReserveStock(new OrderPlaced(orderId, 1m));
            await store.SaveAsync(orderId, saga, default);
            await store.RemoveAsync(orderId, default);
        }

        var ctx2 = fx.CreateContext();
        await using (ctx2.ConfigureAwait(false))
        {
            var any = await ctx2.Set<SagaInstanceEntity>().AnyAsync(e => e.CorrelationKey == orderId.ToString());
            Assert.False(any);
        }
    }
}
