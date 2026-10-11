using System;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Benchmarks;

/// <summary>
/// The generated <c>Handle</c> of a saga's notification handlers on the InMemory store, with every
/// log level disabled. Each operation includes what a handler attempt always costs: the key
/// lock, the per-attempt DI scope and the store and dispatcher it resolves. The duplicate and
/// orphan paths log and return, so a log call that allocates while disabled shows up there
/// directly, #248.
/// </summary>
[MemoryDiagnoser]
public class GeneratedHandlerBenchmarks
{
    private ServiceProvider _sp = null!;
    private INotificationHandler<OrderPlaced> _placed = null!;
    private INotificationHandler<StockReserved> _reserved = null!;
    private INotificationHandler<PaymentCharged> _charged = null!;
    private INotificationHandler<PaymentDeclined> _declined = null!;
    private OrderPlaced _duplicate = null!;
    private PaymentDeclined _orphan = null!;
    private int _nextKey;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddMediator();
        services.TryAddTransient<ReserveStockHandler>();
        services.TryAddTransient<ChargeCustomerHandler>();
        services.TryAddTransient<ShipOrderHandler>();
        services.TryAddTransient<CancelReservationHandler>();
        services.TryAddTransient<RefundPaymentHandler>();
        services.AddSaga().WithOrderSaga();
        _sp = services.BuildServiceProvider();

        _placed = _sp.GetRequiredService<INotificationHandler<OrderPlaced>>();
        _reserved = _sp.GetRequiredService<INotificationHandler<StockReserved>>();
        _charged = _sp.GetRequiredService<INotificationHandler<PaymentCharged>>();
        _declined = _sp.GetRequiredService<INotificationHandler<PaymentDeclined>>();

        // A saga parked in Step1, so another OrderPlaced for it is a duplicate.
        _duplicate = new OrderPlaced(new OrderId(-1), 10m);
        _placed.Handle(_duplicate, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        // No saga ever exists under this key, so PaymentDeclined for it is an orphan.
        _orphan = new PaymentDeclined(new OrderId(-2));
    }

    [GlobalCleanup]
    public void Cleanup() => _sp.Dispose();

    /// <summary>Forward step whose trigger the saga has already consumed; logs at Debug.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask DuplicateEvent() => _placed.Handle(_duplicate, CancellationToken.None);

    /// <summary>Compensation event with no saga to compensate; logs at Warning.</summary>
    [Benchmark]
    public ValueTask OrphanCompensation() => _declined.Handle(_orphan, CancellationToken.None);

    /// <summary>A whole saga: create, two saves, completion and removal, three dispatches.</summary>
    [Benchmark]
    public async ValueTask FullLifecycle()
    {
        var key = new OrderId(Interlocked.Increment(ref _nextKey));
        await _placed.Handle(new OrderPlaced(key, 10m), CancellationToken.None).ConfigureAwait(false);
        await _reserved.Handle(new StockReserved(key), CancellationToken.None).ConfigureAwait(false);
        await _charged.Handle(new PaymentCharged(key), CancellationToken.None).ConfigureAwait(false);
    }
}

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
public partial class OrderSaga
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

internal sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public ValueTask<Unit> Handle(ReserveStockCommand request, CancellationToken ct) => new(Unit.Value);
}

internal sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(ChargeCustomerCommand request, CancellationToken ct) => new(Unit.Value);
}

internal sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public ValueTask<Unit> Handle(ShipOrderCommand request, CancellationToken ct) => new(Unit.Value);
}

internal sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public ValueTask<Unit> Handle(CancelReservationCommand request, CancellationToken ct) => new(Unit.Value);
}

internal sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public ValueTask<Unit> Handle(RefundPaymentCommand request, CancellationToken ct) => new(Unit.Value);
}
