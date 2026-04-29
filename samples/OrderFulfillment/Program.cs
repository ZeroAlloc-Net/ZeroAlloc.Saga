using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderFulfillment.Handlers;
using OrderFulfillment.Saga;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.EfCore;

namespace OrderFulfillment;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Parse a tiny flag set so the demo can be re-launched against the
        // EfCore backend without rebuilding. Default is the InMemory store
        // (matches the v1.0 demo behaviour).
        // Plain System.Array.IndexOf — Linq's Contains extension trips
        // the EPS06 "hidden ReadOnlySpan copy" analyzer in this project.
        var useEfCore = System.Array.IndexOf(args, "--efcore") >= 0;

        Console.WriteLine("=== ZeroAlloc.Saga: OrderFulfillment demo ===");
        Console.WriteLine($"Backend: {(useEfCore ? "EfCore + SQLite (file)" : "InMemory")}");
        Console.WriteLine();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        // Real Mediator + deferred-publish queue + driver + per-command handlers.
        services.AddFakeMediator();

        // SQLite filename is process-local. Recreated on every run so the
        // demo always starts from a fresh schema (no migration to manage).
        const string DemoDbPath = "saga-demo.db";
        if (useEfCore && File.Exists(DemoDbPath)) File.Delete(DemoDbPath);

        var sagaBuilder = services.AddSaga();
        if (useEfCore)
        {
            services.AddDbContext<DemoContext>(opts => opts.UseSqlite($"Data Source={DemoDbPath}"));
            sagaBuilder.WithEfCoreStore<DemoContext>();
        }
        sagaBuilder.AddOrderFulfillmentSaga();

        var sp = services.BuildServiceProvider();

        // For EfCore: synthesise the SagaInstance schema before publishing.
        if (useEfCore)
        {
            using var scope = sp.CreateScope();
            var ctx = scope.ServiceProvider.GetRequiredService<DemoContext>();
            await ctx.Database.EnsureCreatedAsync();
        }

        var driver = sp.GetRequiredService<SagaDriver>();
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        var policy = sp.GetRequiredService<ChargeReactionPolicy>();
        var fakeMediator = sp.GetRequiredService<FakeMediator>();

        // Publish the singleton FakeMediator into the ambient slot the per-command
        // IRequestHandlers read. (Done after BuildServiceProvider so the singleton exists.)
        FakeMediator.Current = fakeMediator;

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
