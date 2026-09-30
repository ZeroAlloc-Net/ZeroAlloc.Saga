using System;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;
using ZeroAlloc.Mediator;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Saga.Orm;
using ZeroAlloc.Saga.Outbox.Orm.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Orm.Tests;

/// <summary>
/// <c>WithOrmOutbox()</c> against a real SQL Server, whose transactions, and whose handling of a
/// failed statement inside one, differ from SQLite's. #197
/// </summary>
/// <remarks>
/// One container for the class. Each test uses its own correlation key and command type, so the
/// tests do not see each other's rows.
/// </remarks>
[Collection(SagaStoreRegistrarCollection.Name)]
public sealed class SqlServerE2ETests(SqlServerE2ETests.Database db) : IClassFixture<SqlServerE2ETests.Database>
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A SQL Server container with the saga and the outbox schema applied.</summary>
    public sealed class Database : IAsyncLifetime
    {
        private readonly MsSqlContainer _container =
            new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

        public IAsyncDbConnection Connection() => new SqlConnection(_container.GetConnectionString()).AsAsync();

        public async Task InitializeAsync()
        {
            await _container.StartAsync().ConfigureAwait(false);
            var connection = Connection();
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                await new MigrationRunner(
                        connection,
                        CombinedMigrations.Of(SagaOrmMigrations.SqlServer, OutboxOrmMigrations.SqlServer),
                        new SqlServerMigrationDialect())
                    .RunAsync(default).ConfigureAwait(false);
            }
        }

        public async Task DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);
    }

    private IAsyncDbConnection Connection() => db.Connection();

    private IHost BuildHost()
    {
        SagaStoreRegistrar.Reset();
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<DispatchedEventCounter>();
                services.AddSingleton<IOutboxDashboardEventPublisher>(
                    sp => sp.GetRequiredService<DispatchedEventCounter>());
                services.AddMediator();
                services.TryAddTransient<ReserveStockHandler>();
                services.TryAddTransient<ChargeCustomerHandler>();
                services.TryAddTransient<ShipOrderHandler>();
                services.TryAddTransient<CancelReservationHandler>();
                services.TryAddTransient<RefundPaymentHandler>();
                services.AddTestSerializers();
                services.AddScoped(_ => Connection());
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
                    .WithOrm(OutboxOrmDialect.SqlServer);
                services.AddSaga()
                    .WithOrmStore(opts =>
                    {
                        opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                        opts.UseExponentialBackoff = false;
                    })
                    .WithOutbox()
                    .WithOrmOutbox()
                    .WithOrderFulfillmentSaga();
            })
            .Build();
    }

    [Fact]
    public async Task Commit_Writes_The_Saga_Row_And_The_Outbox_Row_And_The_Worker_Dispatches_It()
    {
        using var host = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;
        var orderId = new OrderId(9001);

        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>()
                .Publish(new OrderPlaced(orderId, 12m), default);
        }

        Assert.Equal(1, await CountAsync("SagaInstance", orderId));
        Assert.Equal(1, await CountOutboxAsync(typeof(ReserveStockCommand)));

        var counter = host.Services.GetRequiredService<DispatchedEventCounter>();
        await host.StartAsync();
        try
        {
            var deadline = DateTime.UtcNow + WorkerTimeout;
            while (counter.Dispatched < 1)
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("The outbox worker did not dispatch the saga command.");
                await Task.Delay(20);
            }
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Equal(orderId, Assert.Single(ledger.CommandsOfType<ReserveStockCommand>()).OrderId);
    }

    [Fact]
    public async Task A_Stale_Save_Rolls_Back_Its_Outbox_Row()
    {
        using var host = BuildHost();
        var orderId = new OrderId(9002);
        await SeedAsync(host, orderId);

        using var scopeA = host.Services.CreateScope();
        using var scopeB = host.Services.CreateScope();
        var storeA = Store(scopeA);
        var storeB = Store(scopeB);
        var sagaA = await storeA.LoadOrCreateAsync(orderId, default);
        var sagaB = await storeB.LoadOrCreateAsync(orderId, default);
        sagaA.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        sagaB.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.StockReserved);
        await EnlistAsync(scopeA, typeof(ChargeCustomerCommand));
        await EnlistAsync(scopeB, typeof(ChargeCustomerCommand));

        await storeA.SaveAsync(orderId, sagaA, default);
        await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaB, default));

        Assert.Equal(1, await CountOutboxAsync(typeof(ChargeCustomerCommand)));
    }

    [Fact]
    public async Task A_Lost_Insert_Race_Rolls_Back_Its_Outbox_Row()
    {
        // The losing insert violates the primary key inside the store's transaction. The store
        // rolls back before it reports the conflict, so B's row never commits.
        using var host = BuildHost();
        var orderId = new OrderId(9003);

        using var scopeA = host.Services.CreateScope();
        using var scopeB = host.Services.CreateScope();
        var storeA = Store(scopeA);
        var storeB = Store(scopeB);
        var sagaA = Started(await storeA.LoadOrCreateAsync(orderId, default), orderId);
        var sagaB = Started(await storeB.LoadOrCreateAsync(orderId, default), orderId);
        await EnlistAsync(scopeA, typeof(ShipOrderCommand));
        await EnlistAsync(scopeB, typeof(ShipOrderCommand));

        await storeA.SaveAsync(orderId, sagaA, default);
        await Assert.ThrowsAsync<OrmSagaConcurrencyException>(
            async () => await storeB.SaveAsync(orderId, sagaB, default));

        Assert.Equal(1, await CountAsync("SagaInstance", orderId));
        Assert.Equal(1, await CountOutboxAsync(typeof(ShipOrderCommand)));
    }

    private static ISagaStore<OrderFulfillmentSaga, OrderId> Store(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();

    private static OrderFulfillmentSaga Started(OrderFulfillmentSaga saga, OrderId orderId)
    {
        saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced);
        saga.ReserveStock(new OrderPlaced(orderId, 1m));
        return saga;
    }

    private static async Task SeedAsync(IHost host, OrderId orderId)
    {
        using var scope = host.Services.CreateScope();
        var store = Store(scope);
        await store.SaveAsync(orderId, Started(await store.LoadOrCreateAsync(orderId, default), orderId), default);
    }

    // What the outbox dispatcher does when a step returns a command. The payload is not read.
    private static ValueTask EnlistAsync(IServiceScope scope, Type commandType)
        => scope.ServiceProvider.GetRequiredService<ISagaUnitOfWork>()
            .EnlistOutboxRowAsync(commandType.FullName!, new byte[] { 1 }, default);

    private Task<long> CountAsync(string table, OrderId orderId)
        => ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE CorrelationKey = N'{orderId}'");

    private Task<long> CountOutboxAsync(Type commandType)
        => ScalarAsync($"SELECT COUNT(*) FROM OutboxMessages WHERE TypeName = N'{commandType.FullName}'");

    private async Task<long> ScalarAsync(string sql)
    {
        var connection = Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                var scalar = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }
        }
    }
}
