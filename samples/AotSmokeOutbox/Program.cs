using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
///   1. The saga generator's <c>MediatorSagaCommandDispatcher</c> roots
///      <c>ZeroAlloc.Saga.Generated.SagaCommandRegistry</c> via the emitted
///      <c>[DynamicDependency]</c>, so it survives trimming.
///   2. <c>SagaOutboxBuilderExtensions.WithOutbox()</c> locates that registry
///      reflectively at runtime through <c>AppDomain.CurrentDomain.GetAssemblies()</c>
///      and a <c>MethodInfo.Invoke</c> over a non-generic static method —
///      the path the IL2026/IL2075/IL3050 suppressions claim is AOT-safe.
///   3. The outbox dispatch round-trip works: saga step → enqueue (serialised
///      bytes) → poller dispatch (deserialised via DI-resolved
///      <see cref="ISerializer{T}"/>) → mediator <see cref="IMediator.Send"/>.
///
/// If the rooting were missing, the binary would fail at the first poll cycle
/// with "could not locate the generator-emitted SagaCommandRegistry". Exit code
/// 0 from this binary is the load-bearing assertion.
/// </summary>
internal static class Program
{
    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        using var scope = sp.CreateScope();
        foreach (var h in scope.ServiceProvider.GetServices<INotificationHandler<T>>())
            await h.Handle(evt, default).ConfigureAwait(false);
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("AotSmokeOutbox: starting saga + outbox bridge end-to-end under native AOT");

        var counters = new CommandCounters();
        CommandCounters.Current = counters;

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddMediator();

        // Single-instance in-process IOutboxStore — registered as Singleton so
        // the OutboxSagaCommandDispatcher (Scoped) and the poller (also resolves
        // Scoped) see the same underlying entries dictionary across scopes.
        var store = new InProcessOutboxStore();
        services.AddSingleton<IOutboxStore>(store);

        // Per-command AOT-safe ISerializer<T> impls (hand-rolled, no JSON / reflection).
        services.AddSingleton<ISerializer<ReserveStockCommand>, ReserveStockSerializer>();
        services.AddSingleton<ISerializer<ChargeCustomerCommand>, ChargeCustomerSerializer>();
        services.AddSingleton<ISerializer<ShipOrderCommand>, ShipOrderSerializer>();
        services.AddSingleton<ISerializer<CancelReservationCommand>, CancelReservationSerializer>();
        services.AddSingleton<ISerializer<RefundPaymentCommand>, RefundPaymentSerializer>();

        services.AddSaga()
            .WithOutbox()                        // <-- the load-bearing line under AOT
            .AddOrderFulfillmentSaga();

        var sp = services.BuildServiceProvider();

        var orderId = new OrderId(42);

        // Step 1: OrderPlaced → saga handler → outbox-dispatcher serialises and enqueues.
        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        if (counters.Reserve != 0) return Fail($"Expected Reserve=0 before poller drains, got {counters.Reserve} (outbox bridge should not call mediator inline)");
        if (store.Count != 1) return Fail($"Expected 1 outbox entry after OrderPlaced, got {store.Count}");

        // Drive the poller manually (one cycle is enough for this test). The poller
        // resolves SagaCommandRegistry reflectively — this is THE call that fails
        // under PublishAot=true if [DynamicDependency] rooting is missing.
        var poller = sp.GetServices<IHostedService>().OfType<OutboxSagaCommandPoller>().Single();
        await poller.PollOnceAsync(default);
        if (counters.Reserve != 1) return Fail($"Expected Reserve=1 after poller drained 1 entry, got {counters.Reserve}");
        if (store.SucceededCount != 1) return Fail($"Expected 1 succeeded entry after dispatch, got {store.SucceededCount}");

        // Step 2: StockReserved → ChargeCustomer enqueued + dispatched.
        await PublishAsync(sp, new StockReserved(orderId));
        await poller.PollOnceAsync(default);
        if (counters.Charge != 1) return Fail($"Expected Charge=1 after StockReserved + drain, got {counters.Charge}");

        // Step 3: PaymentCharged → ShipOrder enqueued + dispatched; saga completes.
        await PublishAsync(sp, new PaymentCharged(orderId));
        await poller.PollOnceAsync(default);
        if (counters.Ship != 1) return Fail($"Expected Ship=1 after PaymentCharged + drain, got {counters.Ship}");

        if (counters.Cancel != 0) return Fail($"Expected Cancel=0 (no compensation), got {counters.Cancel}");
        if (counters.Refund != 0) return Fail($"Expected Refund=0 (no compensation), got {counters.Refund}");

        if (store.SucceededCount != 3) return Fail($"Expected 3 succeeded outbox entries (one per step), got {store.SucceededCount}");

        Console.WriteLine("AotSmokeOutbox: OK — full saga + outbox bridge dispatch round-trip succeeded under native AOT.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AotSmokeOutbox: FAIL — {message}");
        return 1;
    }
}
