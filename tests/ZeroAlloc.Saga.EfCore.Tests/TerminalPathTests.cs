using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Saga.EfCore.Tests.Fixtures;

namespace ZeroAlloc.Saga.EfCore.Tests;

/// <summary>
/// <see cref="EfCoreSagaStore{TSaga,TKey}.RemoveAsync"/> ends a handler attempt just as
/// <see cref="EfCoreSagaStore{TSaga,TKey}.SaveAsync"/> does, so it commits everything the attempt
/// enlisted in the scoped <see cref="DbContext"/>, such as an outbox row, whether or not a saga
/// row exists, #194. A saga this scope loaded is checked against its row version on both paths.
/// </summary>
public sealed class TerminalPathTests
{
    private static EfCoreSagaStore<OrderFulfillmentSaga, OrderId> CreateStore(DbContext ctx)
        => new(ctx, NullLogger<EfCoreSagaStore<OrderFulfillmentSaga, OrderId>>.Instance);

    private static async Task SaveStartedSagaAsync(SqliteFixture fx, OrderId orderId)
    {
        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var store = CreateStore(ctx);
            var saga = await store.LoadOrCreateAsync(orderId, default).ConfigureAwait(false);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            saga.ReserveStock(new OrderPlaced(orderId, 1m));
            await store.SaveAsync(orderId, saga, default).ConfigureAwait(false);
        }
    }

    private static async Task<bool> RowExistsAsync(SqliteFixture fx, OrderId orderId)
    {
        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var key = orderId.ToString();
            return await ctx.Set<SagaInstanceEntity>()
                .AnyAsync(e => e.CorrelationKey == key).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Remove_WithNoSagaRow_StillCommitsWhatTheScopeEnlisted()
    {
        // The single-step shape of #194: the saga was never saved, so there is no row to delete,
        // but the attempt enlisted other work in the same DbContext. With the outbox bridge that
        // is the step's outbox row; here it is an unrelated tracked insert.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var sagaKey = new OrderId(501);
        var enlistedKey = new OrderId(502);

        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var store = CreateStore(ctx);
            var saga = await store.LoadOrCreateAsync(sagaKey, default);
            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            ctx.Set<SagaInstanceEntity>().Add(new SagaInstanceEntity
            {
                SagaType = "Enlisted",
                CorrelationKey = enlistedKey.ToString(),
                State = [],
                CurrentFsmState = "Step1",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RowVersion = Guid.NewGuid().ToByteArray(),
            });

            await store.RemoveAsync(sagaKey, default);
        }

        Assert.False(await RowExistsAsync(fx, sagaKey));
        Assert.True(await RowExistsAsync(fx, enlistedKey));
    }

    [Fact]
    public async Task Remove_OfALoadedSaga_DeletedBehindOurBack_IsAConflict()
    {
        // Another writer ended the saga after this scope loaded it. Reporting a successful remove
        // would commit this attempt's enlisted commands for a saga that is already over.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(511);
        await SaveStartedSagaAsync(fx, orderId);

        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var store = CreateStore(ctx);
            await store.LoadOrCreateAsync(orderId, default);

            var other = fx.CreateContext();
            await using (other.ConfigureAwait(false))
            {
                await CreateStore(other).RemoveAsync(orderId, default);
            }

            var ex = await Assert.ThrowsAsync<EfCoreSagaConcurrencyException>(
                async () => await store.RemoveAsync(orderId, default).ConfigureAwait(false));
            Assert.IsAssignableFrom<ISagaConcurrencyConflict>(ex);
        }
    }

    [Fact]
    public async Task Remove_OfALoadedSaga_ChangedBehindOurBack_IsARetryableConflict()
    {
        // The delete carries the row version this scope loaded. EF raises a plain
        // DbUpdateConcurrencyException for it; the store must re-throw it as a conflict the
        // generated retry loop recognises.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(521);
        await SaveStartedSagaAsync(fx, orderId);

        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var store = CreateStore(ctx);
            await store.LoadOrCreateAsync(orderId, default);

            var other = fx.CreateContext();
            await using (other.ConfigureAwait(false))
            {
                var otherStore = CreateStore(other);
                var saga = await otherStore.LoadOrCreateAsync(orderId, default);
                saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
                await otherStore.SaveAsync(orderId, saga, default);
            }

            await Assert.ThrowsAsync<EfCoreSagaConcurrencyException>(
                async () => await store.RemoveAsync(orderId, default).ConfigureAwait(false));
        }

        Assert.True(await RowExistsAsync(fx, orderId));
    }

    [Fact]
    public async Task Save_OfALoadedSaga_DeletedBehindOurBack_IsAConflict()
    {
        // Same shape on the save path: the row this scope loaded is gone, so the update is a
        // conflict, not an insert of a second instance under the tracked key.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        var orderId = new OrderId(531);
        await SaveStartedSagaAsync(fx, orderId);

        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var store = CreateStore(ctx);
            var saga = await store.LoadOrCreateAsync(orderId, default);

            var other = fx.CreateContext();
            await using (other.ConfigureAwait(false))
            {
                await CreateStore(other).RemoveAsync(orderId, default);
            }

            saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
            await Assert.ThrowsAsync<EfCoreSagaConcurrencyException>(
                async () => await store.SaveAsync(orderId, saga, default).ConfigureAwait(false));
        }

        Assert.False(await RowExistsAsync(fx, orderId));
    }
}
