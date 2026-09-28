using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.Outbox;
using ZeroAlloc.Serialisation;

namespace AotSmokeOutbox;

/// <summary>
/// AOT-compatibility smoke test for the Saga.Outbox bridge.
///
/// What this proves end-to-end under <c>PublishAot=true</c>:
///   1. <c>With{Saga}()</c> registers the generated <c>GeneratedSagaCommandSource</c>,
///      which references <c>ZeroAlloc.Saga.Generated.SagaCommandRegistry</c> directly, so
///      the registry survives trimming without reflection or a rooting attribute.
///   2. <c>SagaOutboxBuilderExtensions.WithOutbox()</c> registers one outbox dispatcher per
///      command type that source lists.
///   3. A real host runs <c>WithOutbox()</c>'s startup check, then ZeroAlloc.Outbox's
///      <c>OutboxWorkerService</c> dispatches the round trip: saga step → enqueue →
///      worker claim → saga dispatcher → registry deserialise → mediator Send.
///
/// Exit code 0 from this binary is the load-bearing assertion.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(10);

    // Goes through the real IMediator.Publish rather than resolving INotificationHandler<T>
    // directly. Publish overloads are generated per notification type, so a generic helper
    // cannot bind to them (#127).
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
        Console.WriteLine("AotSmokeOutbox: starting saga + outbox bridge end-to-end under native AOT");

        var counters = new CommandCounters();
        CommandCounters.Current = counters;

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var services = builder.Services;
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddMediator();
        // Mediator 4.x: explicit handler registration (AOT-safe; no reflection).
        services.TryAddTransient<ReserveStockHandler>();
        services.TryAddTransient<ChargeCustomerHandler>();
        services.TryAddTransient<ShipOrderHandler>();
        services.TryAddTransient<CancelReservationHandler>();
        services.TryAddTransient<RefundPaymentHandler>();

        // Single-instance in-process IOutboxStore — registered as Singleton so the
        // OutboxSagaCommandDispatcher (Scoped) and the worker's per-batch scopes see the
        // same underlying entries across scopes.
        var store = new InProcessOutboxStore();
        services.AddSingleton<IOutboxStore>(store);

        // Per-command AOT-safe ISerializer<T> impls (hand-rolled, no JSON / reflection).
        services.AddSingleton<ISerializer<ReserveStockCommand>, ReserveStockSerializer>();
        services.AddSingleton<ISerializer<ChargeCustomerCommand>, ChargeCustomerSerializer>();
        services.AddSingleton<ISerializer<ShipOrderCommand>, ShipOrderSerializer>();
        services.AddSingleton<ISerializer<CancelReservationCommand>, CancelReservationSerializer>();
        services.AddSingleton<ISerializer<RefundPaymentCommand>, RefundPaymentSerializer>();

        // ZeroAlloc.Outbox's worker dispatches the saga commands. The documented registration is
        // services.AddOutbox(), but AddOutbox is [RequiresUnreferencedCode] for a reflection-based
        // JSON serializer fallback that saga dispatch never uses, so a PublishAot build cannot call
        // it without a suppression. This registers what the worker needs directly, and so skips
        // OutboxOptionsValidator: 3.0 makes it internal, so the sample cannot register it itself,
        // and invalid OutboxOptions would go unreported here. The options below are valid.
        // Tracked upstream in ZeroAlloc-Net/ZeroAlloc.Outbox#207.
        services.AddOptions<OutboxOptions>().Configure(o => o.PollingInterval = TimeSpan.FromMilliseconds(50));
        services.AddHostedService<OutboxWorkerService>();

        services.AddSaga()
            .WithOutbox()                        // <-- the load-bearing line under AOT
            .WithOrderFulfillmentSaga();

        using var host = builder.Build();
        var sp = host.Services;
        var orderId = new OrderId(42);

        // Step 1: OrderPlaced → saga handler → outbox-dispatcher serialises and enqueues.
        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        if (Volatile.Read(ref counters.Reserve) != 0) return Fail($"Expected Reserve=0 before the worker runs, got {counters.Reserve} (outbox bridge should not call mediator inline)");
        if (store.Count != 1) return Fail($"Expected 1 outbox entry after OrderPlaced, got {store.Count}");

        // Starting the host runs WithOutbox()'s startup check, then the worker. The worker
        // dispatches through the generated command source into SagaCommandRegistry — the
        // calls that would fail under PublishAot=true if trimming removed the registry.
        await host.StartAsync();
        try
        {
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Reserve) == 1)) return Fail($"Expected Reserve=1 after the worker drained 1 entry, got {counters.Reserve}");
            if (!await WaitForAsync(() => store.SucceededCount == 1)) return Fail($"Expected 1 succeeded entry after dispatch, got {store.SucceededCount}");

            // Step 2: StockReserved → ChargeCustomer enqueued + dispatched.
            await PublishAsync(sp, new StockReserved(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Charge) == 1)) return Fail($"Expected Charge=1 after StockReserved, got {counters.Charge}");

            // Step 3: PaymentCharged → ShipOrder enqueued + dispatched; saga completes.
            await PublishAsync(sp, new PaymentCharged(orderId));
            if (!await WaitForAsync(() => Volatile.Read(ref counters.Ship) == 1)) return Fail($"Expected Ship=1 after PaymentCharged, got {counters.Ship}");
            if (!await WaitForAsync(() => store.SucceededCount == 3)) return Fail($"Expected 3 succeeded outbox entries (one per step), got {store.SucceededCount}");
        }
        finally
        {
            await host.StopAsync();
        }

        if (counters.Cancel != 0) return Fail($"Expected Cancel=0 (no compensation), got {counters.Cancel}");
        if (counters.Refund != 0) return Fail($"Expected Refund=0 (no compensation), got {counters.Refund}");

        Console.WriteLine("AotSmokeOutbox: OK — full saga + outbox bridge dispatch round-trip succeeded under native AOT.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AotSmokeOutbox: FAIL — {message}");
        return 1;
    }
}
