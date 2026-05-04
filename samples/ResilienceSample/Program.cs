using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Mediator;
using ZeroAlloc.Resilience;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.Resilience;

namespace ResilienceSample;

/// <summary>
/// Demonstrates <c>ZeroAlloc.Saga.Resilience</c> end-to-end: the saga's first
/// step has a flaky receiver that fails twice before succeeding. Without a
/// resilience layer the saga handler would observe the failure and either
/// throw or trigger compensation; with <c>WithResilience(retry: 5)</c> the
/// dispatcher retries transparently and the saga progresses normally.
/// </summary>
internal static class Program
{
    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        using var scope = sp.CreateScope();
        foreach (var h in scope.ServiceProvider.GetServices<INotificationHandler<T>>())
            await h.Handle(evt, default);
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("ResilienceSample: starting saga with retry policy (max 5 attempts, 10ms backoff)");

        var stats = new FlakyHandlerStats();
        FlakyHandlerStats.Current = stats;

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // Order: WithXxxSaga() registers the default ISagaCommandDispatcher; WithResilience()
        // decorates whatever is currently registered, so call it AFTER WithXxxSaga().
        services.AddSaga()
            .WithOrderFulfillmentSaga()
            .WithResilience(r =>
            {
                r.Retry = new RetryPolicy(
                    maxAttempts: 5,
                    backoffMs: 10,
                    jitter: false,
                    perAttemptTimeoutMs: 0);
            });

        var sp = services.BuildServiceProvider();

        var orderId = new OrderId(42);

        // Step 1: ReserveStock receiver fails twice, succeeds on the third call.
        // The resilience layer retries transparently; the saga handler observes
        // a single successful dispatch.
        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        if (stats.ReserveStockAttempts != 3)
            return Fail($"Expected ReserveStockAttempts == 3 (2 failures + 1 success), got {stats.ReserveStockAttempts}");
        Console.WriteLine($"  Step 1 OK: ReserveStock dispatched after {stats.ReserveStockAttempts} attempts (resilience retried 2 transient failures)");

        // Step 2: ChargeCustomer receiver always succeeds.
        await PublishAsync(sp, new StockReserved(orderId));
        if (stats.ChargeCustomerAttempts != 1)
            return Fail($"Expected ChargeCustomerAttempts == 1, got {stats.ChargeCustomerAttempts}");
        Console.WriteLine($"  Step 2 OK: ChargeCustomer dispatched (no retries needed)");

        // Step 3: ShipOrder receiver always succeeds; saga reaches Completed.
        await PublishAsync(sp, new PaymentCharged(orderId));
        if (stats.ShipOrderAttempts != 1)
            return Fail($"Expected ShipOrderAttempts == 1, got {stats.ShipOrderAttempts}");
        Console.WriteLine($"  Step 3 OK: ShipOrder dispatched, saga complete");

        Console.WriteLine("ResilienceSample: OK — saga progressed through transient failures via the resilience bridge.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"ResilienceSample: FAIL — {message}");
        return 1;
    }
}
