using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFulfillment.Handlers;
using OrderFulfillment.Saga;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

namespace OrderFulfillment;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.WriteLine("=== ZeroAlloc.Saga: OrderFulfillment demo ===");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        // Fake mediator + deferred-publish queue + driver.
        services.AddFakeMediator();

        // The saga framework + the generator-emitted per-saga registration extension.
        services.AddSaga()
            .AddOrderFulfillmentSaga();

        var sp = services.BuildServiceProvider();
        var driver = sp.GetRequiredService<SagaDriver>();
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        var policy = sp.GetRequiredService<ChargeReactionPolicy>();
        var fakeMediator = (FakeMediator)sp.GetRequiredService<IMediator>();

        // ── Scenario 1: happy path — saga completes ─────────────────────
        Console.WriteLine("Scenario 1: order #1 — happy path");
        var order1 = new OrderId(1);
        await driver.PublishAndDrainAsync(new OrderPlaced(order1, 49.95m));
        var saga1 = await manager.GetAsync(order1, default);
        Console.WriteLine($"  saga state after happy path: {(saga1 is null ? "<removed: completed>" : saga1.Fsm.Current.ToString())}");
        Console.WriteLine();

        // ── Scenario 2: payment declined — reverse-cascade compensation ──
        Console.WriteLine("Scenario 2: order #2 — payment declined, compensation cascade fires");
        policy.DeclineNext = true;
        var order2 = new OrderId(2);
        await driver.PublishAndDrainAsync(new OrderPlaced(order2, 199.00m));
        var saga2 = await manager.GetAsync(order2, default);
        Console.WriteLine($"  saga state after compensation: {(saga2 is null ? "<removed: compensated>" : saga2.Fsm.Current.ToString())}");
        Console.WriteLine();

        // ── Scenario 3: orphan failure event — silently ignored ─────────
        Console.WriteLine("Scenario 3: order #3 — orphan PaymentDeclined (no prior saga)");
        var orphan = new OrderId(3);
        await driver.PublishAndDrainAsync(new PaymentDeclined(orphan, "no such order"));
        var orphanSaga = await manager.GetAsync(orphan, default);
        Console.WriteLine($"  saga lookup: {(orphanSaga is null ? "<not found, as expected>" : "<unexpected!>")}");
        Console.WriteLine();

        // ── Scenario 4: manual compensation via ISagaManager.CompensateAsync
        // The saga is parked at Step 1 (StockReserved is suppressed) — an
        // operator decides to abort the workflow without waiting for downstream
        // events. CompensateAsync walks the cascade in reverse from the
        // current state.
        Console.WriteLine("Scenario 4: order #4 — operator-initiated compensation mid-flight");
        fakeMediator.ParkAfterReserveStock = true;
        var order4 = new OrderId(4);
        await driver.PublishAndDrainAsync(new OrderPlaced(order4, 75m));
        Console.WriteLine("  operator aborts — invoking ISagaManager.CompensateAsync()");
        await manager.CompensateAsync(order4, default);
        var saga4 = await manager.GetAsync(order4, default);
        Console.WriteLine($"  saga state after manual compensation: {(saga4 is null ? "<removed>" : saga4.Fsm.Current.ToString())}");
        fakeMediator.ParkAfterReserveStock = false;
        Console.WriteLine();

        Console.WriteLine("=== Demo complete ===");
        return 0;
    }
}
