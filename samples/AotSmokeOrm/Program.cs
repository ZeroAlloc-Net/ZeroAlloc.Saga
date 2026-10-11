using System;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.Orm;
using ZeroAlloc.Saga.Outbox;
using ZeroAlloc.Saga.Outbox.Orm;

namespace AotSmokeOrm;

/// <summary>
/// Native AOT smoke test for the ZeroAlloc.ORM saga backend and its outbox unit of work.
///
/// What this proves under <c>PublishAot=true</c>, against a SQLite file:
///   1. <c>SagaOrmMigrations</c> and <c>OutboxOrmMigrations</c> create both tables through
///      the ORM's <c>MigrationRunner</c>.
///   2. <c>WithOrmStore()</c> loads, inserts, updates and finally deletes the saga row through
///      the generated ORM repository.
///   3. <c>WithOrmOutbox()</c> writes each step's outbox row in the saga store's transaction,
///      and ZeroAlloc.Outbox's worker dispatches it back to the mediator.
///
/// Exit code 0 from this binary is the load-bearing assertion.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(10);

    private static async Task PublishAsync(IServiceProvider sp, OrderPlaced evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, StockReserved evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, PaymentCharged evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, OrderShipped evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WorkerTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("AotSmokeOrm: starting ORM saga store + ORM outbox end-to-end under native AOT");

        // A file, not an in-memory database: the outbox worker opens connections of its own.
        // Pooling is off so the file can be deleted at the end.
        var path = Path.Combine(Path.GetTempPath(), $"aotsmoke-orm-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await MigrateAsync(connectionString);
            return await RunAsync(connectionString);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var connection = new SqliteConnection(connectionString).AsAsync();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            var dialect = new SqliteMigrationDialect();
            await new MigrationRunner(connection, SagaOrmMigrations.Sqlite, dialect).RunAsync(CancellationToken.None).ConfigureAwait(false);
            await new MigrationRunner(connection, OutboxOrmMigrations.Sqlite, dialect).RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunAsync(string connectionString)
    {
        var counters = new CommandCounters();
        CommandCounters.Current = counters;

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var services = builder.Services;
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddMediator();
        services.TryAddTransient<ReserveStockHandler>();
        services.TryAddTransient<ChargeCustomerHandler>();
        services.TryAddTransient<ShipOrderHandler>();
        services.TryAddTransient<CancelReservationHandler>();
        services.TryAddTransient<RefundPaymentHandler>();
        services.TryAddTransient<NotifyCustomerHandler>();

        services.AddReserveStockCommandSerializer();
        services.AddChargeCustomerCommandSerializer();
        services.AddShipOrderCommandSerializer();
        services.AddCancelReservationCommandSerializer();
        services.AddRefundPaymentCommandSerializer();
        services.AddNotifyCustomerCommandSerializer();

        // One connection per scope; the saga store and the outbox store of a scope share it.
        services.AddScoped<IAsyncDbConnection>(_ => new SqliteConnection(connectionString).AsAsync());
        services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50)).WithOrm();
        services.AddSaga()
            .WithOrmStore()
            .WithOutbox()
            .WithOrmOutbox()
            .WithOrderFulfillmentSaga();

        using var host = builder.Build();
        var sp = host.Services;
        var orderId = new OrderId(42);

        // Step 1 commits the saga row and its outbox row together; nothing dispatches inline.
        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        if (await CountAsync(connectionString, "SagaInstance") != 1) return Fail("Expected 1 saga row after OrderPlaced");
        if (await CountAsync(connectionString, "OutboxMessages") != 1) return Fail("Expected 1 outbox row after OrderPlaced");
        if (Volatile.Read(ref counters.Reserve) != 0) return Fail($"Expected Reserve=0 before the worker runs, got {counters.Reserve}");

        await host.StartAsync();
        try
        {
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Reserve) == 1)) return Fail($"Expected Reserve=1, got {counters.Reserve}");

            await PublishAsync(sp, new StockReserved(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Charge) == 1)) return Fail($"Expected Charge=1, got {counters.Charge}");

            await PublishAsync(sp, new PaymentCharged(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Ship) == 1)) return Fail($"Expected Ship=1, got {counters.Ship}");

            // The last step removes the saga row in the same transaction as its outbox row.
            await PublishAsync(sp, new OrderShipped(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Notify) == 1)) return Fail($"Expected Notify=1, got {counters.Notify}");
        }
        finally
        {
            await host.StopAsync();
        }

        if (await CountAsync(connectionString, "SagaInstance") != 0) return Fail("Expected the saga row to be removed on completion");
        if (await CountAsync(connectionString, "OutboxMessages") != 4) return Fail("Expected 4 outbox rows, one per step");
        if (counters.Cancel != 0 || counters.Refund != 0) return Fail("Expected no compensation");

        Console.WriteLine("AotSmokeOrm: OK — ORM saga store + ORM outbox round trip succeeded under native AOT.");
        return 0;
    }

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        var connection = new SqliteConnection(connectionString).AsAsync();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT COUNT(*) FROM " + table;
                var scalar = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AotSmokeOrm: FAIL — {message}");
        return 1;
    }
}
