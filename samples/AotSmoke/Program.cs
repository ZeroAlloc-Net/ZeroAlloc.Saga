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
/// Per-command counters surfaced via an ambient slot so each handler can record
/// without ctor injection (the Mediator dispatcher's no-factory fallback path
/// requires a parameterless ctor, so we avoid DI for handlers).
/// </summary>
internal sealed class CommandCounters
{
    public int Reserve;
    public int Charge;
    public int Ship;
    public int Cancel;
    public int Refund;

    public static CommandCounters? Current { get; set; }
}

// Recording IRequestHandlers — light up real IMediator.Send dispatch end-to-end.
internal sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
    { CommandCounters.Current!.Reserve++; return new(Unit.Value); }
}
internal sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(ChargeCustomerCommand req, CancellationToken ct)
    { CommandCounters.Current!.Charge++; return new(Unit.Value); }
}
internal sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
    { CommandCounters.Current!.Ship++; return new(Unit.Value); }
}
internal sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public ValueTask<Unit> Handle(CancelReservationCommand req, CancellationToken ct)
    { CommandCounters.Current!.Cancel++; return new(Unit.Value); }
}
internal sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public ValueTask<Unit> Handle(RefundPaymentCommand req, CancellationToken ct)
    { CommandCounters.Current!.Refund++; return new(Unit.Value); }
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

        var counters = new CommandCounters();
        CommandCounters.Current = counters;

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        // Real IMediator wired by the Mediator source generator.
        services.AddMediator();

        services.AddSaga()
            .AddOrderFulfillmentSaga();
        var sp = services.BuildServiceProvider();

        var orderId = new OrderId(42);
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();

        // Step 1 → 2 → 3 — full happy path.
        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentCharged(orderId));

        // Assertions: each forward command dispatched exactly once.
        if (counters.Reserve != 1) return Fail($"Expected Reserve=1, got {counters.Reserve}");
        if (counters.Charge != 1) return Fail($"Expected Charge=1, got {counters.Charge}");
        if (counters.Ship != 1) return Fail($"Expected Ship=1, got {counters.Ship}");
        if (counters.Cancel != 0) return Fail($"Expected Cancel=0, got {counters.Cancel}");
        if (counters.Refund != 0) return Fail($"Expected Refund=0, got {counters.Refund}");

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
