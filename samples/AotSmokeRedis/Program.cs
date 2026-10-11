using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.Outbox;
using ZeroAlloc.Saga.Outbox.Redis;
using ZeroAlloc.Saga.Redis;

namespace AotSmokeRedis;

/// <summary>
/// Native AOT smoke test for the Redis saga backend and its Redis-native outbox.
///
/// What this proves under <c>PublishAot=true</c>, against the Redis server named by the
/// <c>REDIS_CONNECTION</c> environment variable:
///   1. <c>WithRedisStore()</c> loads, saves and finally deletes the saga hash under
///      WATCH/MULTI/EXEC.
///   2. <c>WithRedisOutbox()</c> writes each step's outbox entry in the same MULTI/EXEC, and
///      ZeroAlloc.Outbox's worker claims it from <c>RedisOutboxStore</c> and dispatches it.
///
/// CI publishes this binary, which compiles every one of those paths natively, but does not run
/// it: running needs a Redis server. Exit code 0 is the load-bearing assertion when it runs.
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
        var connection = Environment.GetEnvironmentVariable("REDIS_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            return Fail("Set REDIS_CONNECTION to a Redis server, e.g. localhost:6379.");

        Console.WriteLine("AotSmokeRedis: starting Redis saga store + Redis outbox end-to-end under native AOT");

        var counters = new CommandCounters();
        CommandCounters.Current = counters;

        // A fresh prefix per run, so a reused server holds no state from an earlier run.
        var run = Guid.NewGuid().ToString("N");
        using var multiplexer = await ConnectionMultiplexer.ConnectAsync(connection);

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

        services.AddSingleton<IConnectionMultiplexer>(multiplexer);
        services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50));
        services.AddSaga()
            .WithRedisStore(o => o.KeyPrefix = $"aotsmoke:{run}:saga:")
            .WithOutbox()
            .WithRedisOutbox(o => o.KeyPrefix = $"aotsmoke:{run}:outbox:")
            .WithOrderFulfillmentSaga();

        using var host = builder.Build();
        var sp = host.Services;
        var orderId = new OrderId(42);

        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        if (Volatile.Read(ref counters.Reserve) != 0) return Fail($"Expected Reserve=0 before the worker runs, got {counters.Reserve}");
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        if (await manager.GetAsync(orderId, default) is null) return Fail("Expected the saga to be stored after OrderPlaced");

        await host.StartAsync();
        try
        {
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Reserve) == 1)) return Fail($"Expected Reserve=1, got {counters.Reserve}");

            await PublishAsync(sp, new StockReserved(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Charge) == 1)) return Fail($"Expected Charge=1, got {counters.Charge}");

            await PublishAsync(sp, new PaymentCharged(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Ship) == 1)) return Fail($"Expected Ship=1, got {counters.Ship}");

            await PublishAsync(sp, new OrderShipped(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Notify) == 1)) return Fail($"Expected Notify=1, got {counters.Notify}");
        }
        finally
        {
            await host.StopAsync();
        }

        if (await manager.GetAsync(orderId, default) is not null) return Fail("Expected the saga to be removed on completion");
        if (counters.Cancel != 0 || counters.Refund != 0) return Fail("Expected no compensation");

        Console.WriteLine("AotSmokeRedis: OK — Redis saga store + Redis outbox round trip succeeded under native AOT.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AotSmokeRedis: FAIL — {message}");
        return 1;
    }
}
