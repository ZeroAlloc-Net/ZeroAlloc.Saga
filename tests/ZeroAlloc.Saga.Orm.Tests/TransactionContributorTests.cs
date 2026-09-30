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
/// <see cref="IOrmSagaTransactionContributor"/>: every save and removal runs the saga write and
/// every contributor in one transaction, so the contributors' writes commit or roll back with
/// the saga row. #197
/// </summary>
/// <remarks>
/// The contributor here writes one row into a <c>Marker</c> table, standing in for the outbox
/// rows ZeroAlloc.Saga.Outbox.Orm writes. Counting both tables after each operation shows what
/// the database actually committed.
/// </remarks>
public sealed class TransactionContributorTests
{
    private static ServiceProvider BuildProvider(IAsyncDbConnection connection, MarkerLog log, bool fail = false)
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddSingleton(connection);
        services.AddSingleton(log);
        services.AddScoped<IOrmSagaTransactionContributor>(sp =>
            new MarkerContributor(sp.GetRequiredService<MarkerLog>(), fail));
        services.AddSaga()
            .WithOrmStore()
            .WithOrderFulfillmentSaga();
        return services.BuildServiceProvider();
    }

    private static ISagaStore<OrderFulfillmentSaga, OrderId> StoreFrom(ServiceProvider sp)
        => sp.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();

    private static async Task<SqliteFixture> NewDatabaseAsync()
    {
        var fx = new SqliteFixture();
        await fx.MigrateAsync().ConfigureAwait(false);
        await ExecuteAsync(fx, "CREATE TABLE Marker (Id INTEGER PRIMARY KEY AUTOINCREMENT)").ConfigureAwait(false);
        return fx;
    }

    private static OrderFulfillmentSaga Started(OrderFulfillmentSaga saga, OrderId orderId)
    {
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        saga.ReserveStock(new OrderPlaced(orderId, 1m));
        return saga;
    }

    [Fact]
    public async Task Insert_Commits_The_Saga_Row_And_The_Contributed_Row_Together()
    {
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        await using var sp = BuildProvider(await fx.ConnectAsync(), log);
        var store = StoreFrom(sp);
        var orderId = new OrderId(1);

        await store.SaveAsync(orderId, Started(await store.LoadOrCreateAsync(orderId, default), orderId), default);

        Assert.Equal(1, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(1, await CountAsync(fx, "Marker"));
        Assert.Equal(1, log.Calls);
    }

    [Fact]
    public async Task Update_And_Remove_Each_Commit_Their_Contributed_Row()
    {
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        await using var sp = BuildProvider(await fx.ConnectAsync(), log);
        var store = StoreFrom(sp);
        var orderId = new OrderId(2);

        var saga = Started(await store.LoadOrCreateAsync(orderId, default), orderId);
        await store.SaveAsync(orderId, saga, default);
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        await store.SaveAsync(orderId, saga, default);
        await store.RemoveAsync(orderId, default);

        Assert.Equal(0, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(3, await CountAsync(fx, "Marker"));
    }

    [Fact]
    public async Task Remove_Of_A_Saga_That_Was_Never_Saved_Still_Commits_The_Contributed_Row()
    {
        // The single-step shape, #194: one event starts and completes the saga, so the handler
        // ends in RemoveAsync with no row to delete. The enlisted writes must still commit.
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        await using var sp = BuildProvider(await fx.ConnectAsync(), log);
        var store = StoreFrom(sp);
        var orderId = new OrderId(3);

        await store.LoadOrCreateAsync(orderId, default);
        await store.RemoveAsync(orderId, default);

        Assert.Equal(0, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(1, await CountAsync(fx, "Marker"));
    }

    [Fact]
    public async Task Remove_Without_A_Prior_Load_Commits_The_Contributed_Row()
    {
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        await using var sp = BuildProvider(await fx.ConnectAsync(), log);

        await StoreFrom(sp).RemoveAsync(new OrderId(4), default);

        Assert.Equal(1, await CountAsync(fx, "Marker"));
    }

    [Fact]
    public async Task A_Stale_Update_Commits_Neither_Row()
    {
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        var orderId = new OrderId(5);
        await using var spA = BuildProvider(await fx.ConnectAsync(), log);
        await using var spB = BuildProvider(await fx.ConnectAsync(), log);
        var storeA = StoreFrom(spA);
        var storeB = StoreFrom(spB);
        await storeA.SaveAsync(orderId, Started(await storeA.LoadOrCreateAsync(orderId, default), orderId), default);

        var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
        var sagaB = await storeB.LoadOrCreateAsync(orderId, default);
        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sagaB.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        await storeA.SaveAsync(orderId, sagaA, default);

        await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaB, default).ConfigureAwait(false));

        // The seed and A's update each committed one marker; B's conflict committed none, and
        // never reached the contributor.
        Assert.Equal(2, await CountAsync(fx, "Marker"));
        Assert.Equal(2, log.Calls);
    }

    [Fact]
    public async Task A_Lost_Insert_Race_Rolls_Back_And_Commits_No_Contributed_Row()
    {
        // A primary-key violation inside a transaction aborts it on some databases, so the
        // store must roll back before it reports the conflict, and nothing may commit.
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        var orderId = new OrderId(6);
        await using var spA = BuildProvider(await fx.ConnectAsync(), log);
        await using var spB = BuildProvider(await fx.ConnectAsync(), log);
        var storeA = StoreFrom(spA);
        var storeB = StoreFrom(spB);
        var sagaA = Started(await storeA.LoadOrCreateAsync(orderId, default), orderId);
        var sagaB = Started(await storeB.LoadOrCreateAsync(orderId, default), orderId);

        await storeA.SaveAsync(orderId, sagaA, default);
        await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaB, default).ConfigureAwait(false));

        Assert.Equal(1, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(1, await CountAsync(fx, "Marker"));

        // B's connection is usable afterwards: its transaction did not stay open.
        await storeB.LoadOrCreateAsync(orderId, default);
        await storeB.RemoveAsync(orderId, default);
        Assert.Equal(0, await CountAsync(fx, "SagaInstance"));
    }

    [Fact]
    public async Task A_Stale_Remove_Commits_No_Contributed_Row_And_Keeps_Their_Saga()
    {
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        var orderId = new OrderId(7);
        await using var spA = BuildProvider(await fx.ConnectAsync(), log);
        await using var spB = BuildProvider(await fx.ConnectAsync(), log);
        var storeA = StoreFrom(spA);
        var storeB = StoreFrom(spB);
        await storeA.SaveAsync(orderId, Started(await storeA.LoadOrCreateAsync(orderId, default), orderId), default);

        await storeB.LoadOrCreateAsync(orderId, default);
        var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        await storeA.SaveAsync(orderId, sagaA, default);

        await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.RemoveAsync(orderId, default).ConfigureAwait(false));

        Assert.Equal(1, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(2, await CountAsync(fx, "Marker"));
    }

    [Fact]
    public async Task A_Failing_Contributor_Rolls_Back_The_Saga_Write()
    {
        // Any failure inside the transaction discards everything, the saga row included, so a
        // saga never advances past a step whose side effects were not recorded.
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        await using var sp = BuildProvider(await fx.ConnectAsync(), log, fail: true);
        var store = StoreFrom(sp);
        var orderId = new OrderId(8);
        var saga = Started(await store.LoadOrCreateAsync(orderId, default), orderId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.SaveAsync(orderId, saga, default).ConfigureAwait(false));

        Assert.Equal(0, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(0, await CountAsync(fx, "Marker"));
    }

    [Fact]
    public async Task A_Failed_Save_Leaves_The_Store_Planning_An_Insert()
    {
        // The rolled-back insert never happened, so the store must not remember its row version.
        // A retry on the same store has to insert again, not update a row that does not exist.
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        var connection = await fx.ConnectAsync();
        await using var failing = BuildProvider(connection, log, fail: true);
        var store = StoreFrom(failing);
        var orderId = new OrderId(9);
        var saga = Started(await store.LoadOrCreateAsync(orderId, default), orderId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await store.SaveAsync(orderId, saga, default).ConfigureAwait(false));
        log.StopFailing = true;
        await store.SaveAsync(orderId, saga, default);

        Assert.Equal(1, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(1, await CountAsync(fx, "Marker"));
    }

    [Fact]
    public async Task A_Closed_Connection_Is_Opened_For_The_Transaction_And_Closed_Again()
    {
        // The ORM opens a closed connection for each statement and closes it afterwards. The
        // store's transaction follows the same rule, so an application that hands out closed
        // connections keeps working.
        await using var fx = await NewDatabaseAsync();
        var log = new MarkerLog();
        var connection = await fx.ConnectAsync();
        await connection.CloseAsync();
        await using var sp = BuildProvider(connection, log);
        var store = StoreFrom(sp);
        var orderId = new OrderId(10);

        await store.SaveAsync(orderId, Started(await store.LoadOrCreateAsync(orderId, default), orderId), default);

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Equal(1, await CountAsync(fx, "SagaInstance"));
        Assert.Equal(1, await CountAsync(fx, "Marker"));
    }

    private static async Task ExecuteAsync(SqliteFixture fx, string sql)
    {
        var connection = await fx.ConnectAsync().ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> CountAsync(SqliteFixture fx, string table)
    {
        var connection = await fx.ConnectAsync().ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM " + table;
                var scalar = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }
        }
    }

    private sealed class MarkerLog
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool StopFailing { get; set; }

        public void Record() => Interlocked.Increment(ref _calls);
    }

    // Writes one Marker row inside the saga store's transaction, then optionally fails.
    private sealed class MarkerContributor(MarkerLog log, bool fail) : IOrmSagaTransactionContributor
    {
        public async ValueTask ContributeAsync(IAsyncDbTransaction transaction, CancellationToken ct)
        {
            var cmd = transaction.Connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO Marker DEFAULT VALUES";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            log.Record();
            if (fail && !log.StopFailing)
                throw new InvalidOperationException("The contributor failed after writing its row.");
        }
    }
}
