using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

public sealed partial record ReserveStockCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed partial record ChargeCustomerCommand(OrderId OrderId, decimal Total) : IRequest;
public sealed partial record ShipOrderCommand(OrderId OrderId) : IRequest;
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

// ── Second saga: primitive Guid key + readonly record struct commands ──
// Covers the value-type shapes the saga above does not: a primitive correlation
// key in the store / lock dictionaries, and struct commands flowing through the
// generated MediatorSagaCommandDispatcher.DispatchAsync<TCommand> type switch.

public sealed record RefundRequested(Guid RefundId, decimal Amount) : INotification;
public sealed record RefundApproved(Guid RefundId) : INotification;

public readonly record struct ValidateRefundCommand(Guid Id, decimal Amount) : IRequest;
public readonly record struct IssueRefundCommand(Guid Id, decimal Amount) : IRequest;

[Saga]
public partial class RefundSaga
{
    public Guid RefundId { get; private set; }
    public decimal Amount { get; private set; }

    [CorrelationKey] public Guid Correlation(RefundRequested e) => e.RefundId;
    [CorrelationKey] public Guid Correlation(RefundApproved e) => e.RefundId;

    [Step(Order = 1)]
    public ValidateRefundCommand ValidateRefund(RefundRequested e)
    {
        RefundId = e.RefundId;
        Amount = e.Amount;
        return new ValidateRefundCommand(e.RefundId, e.Amount);
    }

    [Step(Order = 2)]
    public IssueRefundCommand IssueRefund(RefundApproved e) => new(RefundId, Amount);
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
    public int ValidateRefund;
    public int IssueRefund;
    public ValidateRefundCommand LastValidateRefund;
    public IssueRefundCommand LastIssueRefund;

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

internal sealed class ValidateRefundHandler : IRequestHandler<ValidateRefundCommand, Unit>
{
    public ValueTask<Unit> Handle(ValidateRefundCommand req, CancellationToken ct)
    {
        var c = CommandCounters.Current!;
        c.ValidateRefund++;
        c.LastValidateRefund = req;
        return new(Unit.Value);
    }
}
internal sealed class IssueRefundHandler : IRequestHandler<IssueRefundCommand, Unit>
{
    public ValueTask<Unit> Handle(IssueRefundCommand req, CancellationToken ct)
    {
        var c = CommandCounters.Current!;
        c.IssueRefund++;
        c.LastIssueRefund = req;
        return new(Unit.Value);
    }
}

internal static class Program
{
    // Publishes through the real IMediator, which is the whole point of this smoke test:
    // it proves the documented journey works under Native AOT, not just that the generated
    // handler can be invoked by hand.
    //
    // This used to resolve INotificationHandler<T> from DI and call Handle directly. That
    // bypassed IMediator entirely and masked #127 — IMediator.Publish could not reach a saga
    // at all. IMediator's Publish overloads are generated per notification type, so a generic
    // helper cannot bind to them; hence one overload per event.
    private static async Task PublishAsync(IServiceProvider sp, OrderPlaced evt)
        => await sp.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);

    private static async Task PublishAsync(IServiceProvider sp, StockReserved evt)
        => await sp.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);

    private static async Task PublishAsync(IServiceProvider sp, PaymentCharged evt)
        => await sp.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);

    private static async Task PublishAsync(IServiceProvider sp, RefundRequested evt)
        => await sp.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);

    private static async Task PublishAsync(IServiceProvider sp, RefundApproved evt)
        => await sp.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);

    private static async Task<int> Main()
    {
        Console.WriteLine("AotSmoke: starting saga end-to-end");

        var counters = new CommandCounters();
        CommandCounters.Current = counters;

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        // Real IMediator wired by the Mediator source generator. Mediator 4.x no
        // longer auto-discovers handlers — register each one explicitly so the
        // AOT publish stays trim/reflection-free (RegisterHandlersFromAssembly
        // is [RequiresUnreferencedCode]).
        services.AddMediator();
        services.TryAddTransient<ReserveStockHandler>();
        services.TryAddTransient<ChargeCustomerHandler>();
        services.TryAddTransient<ShipOrderHandler>();
        services.TryAddTransient<CancelReservationHandler>();
        services.TryAddTransient<RefundPaymentHandler>();
        services.TryAddTransient<ValidateRefundHandler>();
        services.TryAddTransient<IssueRefundHandler>();

        services.AddSaga()
            .WithOrderFulfillmentSaga()
            .WithRefundSaga();
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

        return await RunRefundSagaAsync(sp, counters).ConfigureAwait(false);
    }

    // Guid-keyed saga whose steps send readonly record struct commands. Asserts the exact
    // command values reach the handlers, so a struct lost or defaulted on the generic
    // dispatch path fails here rather than passing as "no exception".
    private static async Task<int> RunRefundSagaAsync(IServiceProvider sp, CommandCounters counters)
    {
        var refundId = new Guid("6f1c2a9e-3b4d-4e5f-8a7b-9c0d1e2f3a4b");
        const decimal amount = 37.25m;
        var manager = sp.GetRequiredService<ISagaManager<RefundSaga, Guid>>();

        await PublishAsync(sp, new RefundRequested(refundId, amount));

        if (counters.ValidateRefund != 1) return Fail($"Expected ValidateRefund=1, got {counters.ValidateRefund}");
        if (counters.LastValidateRefund != new ValidateRefundCommand(refundId, amount))
            return Fail($"ValidateRefund got {counters.LastValidateRefund}");
        if (counters.IssueRefund != 0) return Fail($"Expected IssueRefund=0 after step 1, got {counters.IssueRefund}");

        // Mid-flight the Guid-keyed store holds the saga with the state step 1 wrote.
        var inFlight = await manager.GetAsync(refundId, default);
        if (inFlight is null) return Fail("RefundSaga should be in the store after step 1");
        if (inFlight.RefundId != refundId || inFlight.Amount != amount)
            return Fail($"RefundSaga state after step 1: {inFlight.RefundId} / {inFlight.Amount}");
        if (await manager.GetAsync(Guid.Empty, default) is not null)
            return Fail("RefundSaga lookup by Guid.Empty should find nothing");

        await PublishAsync(sp, new RefundApproved(refundId));

        if (counters.ValidateRefund != 1) return Fail($"Expected ValidateRefund=1, got {counters.ValidateRefund}");
        if (counters.IssueRefund != 1) return Fail($"Expected IssueRefund=1, got {counters.IssueRefund}");
        if (counters.LastIssueRefund != new IssueRefundCommand(refundId, amount))
            return Fail($"IssueRefund got {counters.LastIssueRefund}");

        if (await manager.GetAsync(refundId, default) is not null)
            return Fail("RefundSaga should have been removed from the store after step 2");

        Console.WriteLine($"AotSmoke: OK — Guid-keyed RefundSaga sent struct commands {counters.LastIssueRefund} and completed.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AotSmoke: FAIL — {message}");
        return 1;
    }
}
