using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

// ── Minimal saga shape — kept self-contained to make AOT review trivial ──

namespace AotSmoke;

public readonly record struct OrderId(int V) : IEquatable<OrderId>;

public sealed record OrderPlaced(OrderId OrderId, decimal Total) : INotification;
public sealed record StockReserved(OrderId OrderId) : INotification;
public sealed record PaymentCharged(OrderId OrderId) : INotification;
public sealed record PaymentDeclined(OrderId OrderId) : INotification;

public sealed record ReserveStockCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed record ChargeCustomerCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed record ShipOrderCommand(OrderId OrderId) : IRequest;
public sealed record CancelReservationCommand(OrderId OrderId) : IRequest;
public sealed record RefundPaymentCommand(OrderId OrderId) : IRequest;

[Saga]
public partial class OrderFulfillmentSaga
{
    public OrderId OrderId { get; private set; }
    public decimal Total { get; private set; }

    [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentCharged e) => e.OrderId;
    [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;

    [Step(Order = 1, Compensate = nameof(CancelReservation))]
    public ReserveStockCommand ReserveStock(OrderPlaced e)
    {
        OrderId = e.OrderId;
        Total = e.Total;
        return new ReserveStockCommand(e.OrderId, e.Total);
    }

    [Step(Order = 2, Compensate = nameof(RefundPayment), CompensateOn = typeof(PaymentDeclined))]
    public ChargeCustomerCommand ChargeCustomer(StockReserved e) => new(OrderId, Total);

    [Step(Order = 3)]
    public ShipOrderCommand ShipOrder(PaymentCharged e) => new(OrderId);

    public CancelReservationCommand CancelReservation() => new(OrderId);
    public RefundPaymentCommand RefundPayment() => new(OrderId);
}

/// <summary>
/// Recording mediator — counts every command it sees so the smoke test
/// can assert against the ledger.
/// </summary>
internal sealed class RecordingMediator : IMediator
{
    public int Reserve { get; private set; }
    public int Charge { get; private set; }
    public int Ship { get; private set; }
    public int Cancel { get; private set; }
    public int Refund { get; private set; }

    public ValueTask<Unit> Send(ReserveStockCommand req, CancellationToken ct = default) { Reserve++; return new(Unit.Value); }
    public ValueTask<Unit> Send(ChargeCustomerCommand req, CancellationToken ct = default) { Charge++; return new(Unit.Value); }
    public ValueTask<Unit> Send(ShipOrderCommand req, CancellationToken ct = default) { Ship++; return new(Unit.Value); }
    public ValueTask<Unit> Send(CancelReservationCommand req, CancellationToken ct = default) { Cancel++; return new(Unit.Value); }
    public ValueTask<Unit> Send(RefundPaymentCommand req, CancellationToken ct = default) { Refund++; return new(Unit.Value); }
}

internal static class Program
{
    // Small helper that resolves the saga's generated INotificationHandler<T>
    // from DI and invokes Handle directly. This is the same pattern the
    // runtime test suite uses (see tests/.../RuntimeTests.cs PublishAsync).
    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        foreach (var h in sp.GetServices<INotificationHandler<T>>())
            await h.Handle(evt, default).ConfigureAwait(false);
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("AotSmoke: starting saga end-to-end");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<RecordingMediator>();
        services.AddSingleton<IMediator>(sp => sp.GetRequiredService<RecordingMediator>());
        services.AddSaga()
            .AddOrderFulfillmentSaga();
        var sp = services.BuildServiceProvider();

        var orderId = new OrderId(42);
        var mediator = sp.GetRequiredService<RecordingMediator>();
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();

        // Step 1 → 2 → 3 — full happy path.
        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentCharged(orderId));

        // Assertions: each forward command dispatched exactly once.
        if (mediator.Reserve != 1) return Fail($"Expected Reserve=1, got {mediator.Reserve}");
        if (mediator.Charge != 1) return Fail($"Expected Charge=1, got {mediator.Charge}");
        if (mediator.Ship != 1) return Fail($"Expected Ship=1, got {mediator.Ship}");
        if (mediator.Cancel != 0) return Fail($"Expected Cancel=0, got {mediator.Cancel}");
        if (mediator.Refund != 0) return Fail($"Expected Refund=0, got {mediator.Refund}");

        // Saga removed from store after Step 3 (terminal Completed state).
        var saga = await manager.GetAsync(orderId, default);
        if (saga is not null) return Fail("Saga should have been removed from the store after Step 3");

        Console.WriteLine("AotSmoke: OK — saga reached Completed and was removed from the store.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AotSmoke: FAIL — {message}");
        return 1;
    }
}
