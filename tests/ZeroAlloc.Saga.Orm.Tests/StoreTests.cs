using System;
using System.Data.Async;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga.Orm.Tests.Fixtures;

namespace ZeroAlloc.Saga.Orm.Tests;

/// <summary>
/// Persistence and optimistic-concurrency behaviour of the ZeroAlloc.ORM saga
/// backend, against a real SQLite database with the schema applied by the ORM's
/// own MigrationRunner.
/// </summary>
/// <remarks>
/// Everything is driven through the public DI surface rather than by newing the
/// store, matching how the rest of this repository tests its backends and
/// keeping WithOrmStore's registration on the tested path.
/// </remarks>
public sealed class StoreTests
{
    private static ServiceProvider BuildProvider(IAsyncDbConnection connection)
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddSingleton(connection);
        services.AddSaga()
            .WithOrmStore(opts =>
            {
                opts.MaxRetryAttempts = 3;
                opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                opts.UseExponentialBackoff = false;
            })
            .WithOrderFulfillmentSaga();
        return services.BuildServiceProvider();
    }

    private static ISagaStore<OrderFulfillmentSaga, OrderId> StoreFrom(ServiceProvider sp)
        => sp.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();

    [Fact]
    public async Task TryLoad_Returns_Null_When_No_Row_Exists()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();

        var connection = await fx.ConnectAsync();
        await using var sp = BuildProvider(connection);

        Assert.Null(await StoreFrom(sp).TryLoadAsync(new OrderId(1), default));
    }

    [Fact]
    public async Task Save_Then_Load_Round_Trips_State_And_Fsm_State()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();

        var connection = await fx.ConnectAsync();
        await using var sp = BuildProvider(connection);
        var store = StoreFrom(sp);
        var orderId = new OrderId(42);

        var saga = await store.LoadOrCreateAsync(orderId, default);
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        saga.ReserveStock(new OrderPlaced(orderId, 19.95m));
        await store.SaveAsync(orderId, saga, default);

        var loaded = await store.TryLoadAsync(orderId, default);

        Assert.NotNull(loaded);
        Assert.Equal(orderId, loaded!.OrderId);
        Assert.Equal(19.95m, loaded.Total);
        Assert.Equal(saga.Fsm.Current, loaded.Fsm.Current);
    }

    [Fact]
    public async Task Save_Twice_Updates_In_Place_Rather_Than_Inserting()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();

        var connection = await fx.ConnectAsync();
        await using var sp = BuildProvider(connection);
        var store = StoreFrom(sp);
        var orderId = new OrderId(7);

        var saga = await store.LoadOrCreateAsync(orderId, default);
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        saga.ReserveStock(new OrderPlaced(orderId, 5m));
        await store.SaveAsync(orderId, saga, default);

        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        saga.ChargeCustomer(new StockReserved(orderId));
        await store.SaveAsync(orderId, saga, default);

        Assert.Equal(1, await CountRowsAsync(fx));
    }

    [Fact]
    public async Task Remove_Deletes_The_Row()
    {
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();

        var connection = await fx.ConnectAsync();
        await using var sp = BuildProvider(connection);
        var store = StoreFrom(sp);
        var orderId = new OrderId(9);

        var saga = await store.LoadOrCreateAsync(orderId, default);
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        saga.ReserveStock(new OrderPlaced(orderId, 1m));
        await store.SaveAsync(orderId, saga, default);
        Assert.Equal(1, await CountRowsAsync(fx));

        await store.RemoveAsync(orderId, default);

        Assert.Equal(0, await CountRowsAsync(fx));
        Assert.Null(await store.TryLoadAsync(orderId, default));
    }

    [Fact]
    public async Task Stale_Write_Throws_A_Conflict_The_Retry_Loop_Recognises()
    {
        // Two connections load the same saga; the first save rotates the row
        // version, so the second matches zero rows and must surface as a
        // conflict rather than silently losing the update.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var orderId = new OrderId(101);

        var seedConnection = await fx.ConnectAsync();
        await using (var seedSp = BuildProvider(seedConnection))
        {
            var seedStore = StoreFrom(seedSp);
            var seed = await seedStore.LoadOrCreateAsync(orderId, default);
            seed.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
            seed.ReserveStock(new OrderPlaced(orderId, 1m));
            await seedStore.SaveAsync(orderId, seed, default);
        }

        var connectionA = await fx.ConnectAsync();
        var connectionB = await fx.ConnectAsync();
        await using var spA = BuildProvider(connectionA);
        await using var spB = BuildProvider(connectionB);
        var storeA = StoreFrom(spA);
        var storeB = StoreFrom(spB);

        var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
        var sagaB = await storeB.LoadOrCreateAsync(orderId, default);

        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sagaA.ChargeCustomer(new StockReserved(orderId));
        sagaB.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sagaB.ChargeCustomer(new StockReserved(orderId));

        await storeA.SaveAsync(orderId, sagaA, default);

        var ex = await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaB, default).ConfigureAwait(false));

        // The marker interface is the contract the generated retry loop tests
        // for. Asserting it here is what keeps this store on the retry path.
        Assert.IsAssignableFrom<ISagaConcurrencyConflict>(ex);
        Assert.Equal(orderId.ToString(), ex.CorrelationKey);
    }

    [Fact]
    public async Task Losing_The_Insert_Race_Is_Reported_As_A_Conflict()
    {
        // Neither store has seen a row, so both plan an insert. The second one
        // violates the primary key, which is a conflict rather than a fault:
        // reloading finds the winner's row and the saga proceeds.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var orderId = new OrderId(202);

        var connectionA = await fx.ConnectAsync();
        var connectionB = await fx.ConnectAsync();
        await using var spA = BuildProvider(connectionA);
        await using var spB = BuildProvider(connectionB);
        var storeA = StoreFrom(spA);
        var storeB = StoreFrom(spB);

        var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
        var sagaB = await storeB.LoadOrCreateAsync(orderId, default);

        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        sagaA.ReserveStock(new OrderPlaced(orderId, 2m));
        sagaB.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        sagaB.ReserveStock(new OrderPlaced(orderId, 3m));

        await storeA.SaveAsync(orderId, sagaA, default);

        var ex = await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaB, default).ConfigureAwait(false));

        Assert.IsAssignableFrom<ISagaConcurrencyConflict>(ex);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(1, await CountRowsAsync(fx));
    }

    [Fact]
    public async Task Saving_A_Saga_Deleted_Behind_Our_Back_Is_A_Conflict()
    {
        // The row version cannot match a row that no longer exists, so the
        // update affects nothing. Reporting success here would silently
        // discard the saga's progress.
        await using var fx = new SqliteFixture();
        await fx.MigrateAsync();
        var orderId = new OrderId(303);

        var connectionA = await fx.ConnectAsync();
        var connectionB = await fx.ConnectAsync();
        await using var spA = BuildProvider(connectionA);
        await using var spB = BuildProvider(connectionB);
        var storeA = StoreFrom(spA);
        var storeB = StoreFrom(spB);

        var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        sagaA.ReserveStock(new OrderPlaced(orderId, 4m));
        await storeA.SaveAsync(orderId, sagaA, default);

        // B loads it, then A removes it underneath.
        await storeB.LoadOrCreateAsync(orderId, default);
        await storeA.RemoveAsync(orderId, default);

        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);

        await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaA, default).ConfigureAwait(false));
    }

    [Fact]
    public void Configuring_A_Second_Durable_Store_Is_Rejected()
    {
        var services = new ServiceCollection();
        services.AddMediator();

        var builder = services.AddSaga().WithOrmStore();

        Assert.Throws<InvalidOperationException>(() => builder.WithOrmStore());
    }

    private static async Task<long> CountRowsAsync(SqliteFixture fx)
    {
        var connection = await fx.ConnectAsync().ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM SagaInstance";
                var scalar = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }
        }
    }
}
