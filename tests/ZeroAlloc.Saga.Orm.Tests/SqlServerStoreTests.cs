using System.Data.Async;
using System.Data.Async.Adapters;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using ZeroAlloc.Mediator;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Saga.Orm.Tests.Fixtures;

namespace ZeroAlloc.Saga.Orm.Tests;

/// <summary>
/// The same store against a real SQL Server, proving the SqlServer DDL and the
/// store's SQL actually work together rather than merely compiling.
/// </summary>
public sealed class SqlServerStoreTests : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        var conn = await ConnectAsync().ConfigureAwait(false);
        await using (conn.ConfigureAwait(false))
        {
            await new MigrationRunner(conn, SagaOrmMigrations.SqlServer, new SqlServerMigrationDialect())
                .RunAsync(default).ConfigureAwait(false);
        }
    }

    public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    private async Task<IAsyncDbConnection> ConnectAsync()
    {
        var raw = new SqlConnection(_container.GetConnectionString());
        var c = raw.AsAsync();
        await c.OpenAsync().ConfigureAwait(false);
        return c;
    }

    private async Task<ServiceProvider> ProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddMediator();
        services.AddSingleton(await ConnectAsync().ConfigureAwait(false));
        services.AddSaga().WithOrmStore().WithOrderFulfillmentSaga();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Save_Then_Load_Round_Trips_On_SqlServer()
    {
        await using var sp = await ProviderAsync();
        var store = sp.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();
        var id = new OrderId(1);

        var saga = await store.LoadOrCreateAsync(id, default);
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        saga.ReserveStock(new OrderPlaced(id, 12.5m));
        await store.SaveAsync(id, saga, default);

        var loaded = await store.TryLoadAsync(id, default);

        Assert.NotNull(loaded);
        Assert.Equal(12.5m, loaded!.Total);
        Assert.Equal(saga.Fsm.Current, loaded.Fsm.Current);
    }

    [Fact]
    public async Task Stale_Write_Is_A_Conflict_On_SqlServer()
    {
        // VARBINARY(16) round-tripping is what makes the row-version predicate
        // work; if it silently widened or truncated, this would stop detecting.
        await using var spSeed = await ProviderAsync();
        var seed = spSeed.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();
        var id = new OrderId(2);
        var s = await seed.LoadOrCreateAsync(id, default);
        s.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        s.ReserveStock(new OrderPlaced(id, 1m));
        await seed.SaveAsync(id, s, default);

        await using var spA = await ProviderAsync();
        await using var spB = await ProviderAsync();
        var a = spA.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();
        var b = spB.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();

        var sa = await a.LoadOrCreateAsync(id, default);
        var sb = await b.LoadOrCreateAsync(id, default);
        sa.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sa.ChargeCustomer(new StockReserved(id));
        sb.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sb.ChargeCustomer(new StockReserved(id));

        await a.SaveAsync(id, sa, default);

        await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await b.SaveAsync(id, sb, default).ConfigureAwait(false));
    }

    [Fact]
    public async Task Maximum_Length_Keys_Fit_The_Index()
    {
        // SQL Server only WARNS when an index key's maximum width exceeds the
        // limit, then fails at INSERT once a real row is too long. Tests using
        // short saga types and correlation keys therefore pass against a table
        // that will break in production. This inserts at the declared widths.
        var conn = await ConnectAsync();
        await using (conn.ConfigureAwait(false))
        {
            var cmd = conn.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = """
                    INSERT INTO SagaInstance
                        (SagaType, CorrelationKey, State, CurrentFsmState, RowVersion, CreatedAt, UpdatedAt)
                    VALUES (@t, @k, @s, @f, @r, @c, @u)
                    """;
                Add(cmd, "@t", new string('T', 512));
                Add(cmd, "@k", new string('K', 256));
                Add(cmd, "@s", new byte[] { 1, 2, 3 });
                Add(cmd, "@f", "SomeState");
                Add(cmd, "@r", Guid.NewGuid().ToByteArray());
                Add(cmd, "@c", DateTimeOffset.UtcNow);
                Add(cmd, "@u", DateTimeOffset.UtcNow);

                var affected = await cmd.ExecuteNonQueryAsync(default);
                Assert.Equal(1, affected);
            }
        }

        static void Add(System.Data.Async.IAsyncDbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
    }

    [Fact]
    public async Task Remove_Deletes_The_Row_On_SqlServer()
    {
        await using var sp = await ProviderAsync();
        var store = sp.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();
        var id = new OrderId(3);
        var saga = await store.LoadOrCreateAsync(id, default);
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        saga.ReserveStock(new OrderPlaced(id, 1m));
        await store.SaveAsync(id, saga, default);

        await store.RemoveAsync(id, default);

        Assert.Null(await store.TryLoadAsync(id, default));
    }
}
