using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using OrderFulfillment.Saga;
using ZeroAlloc.Mediator;

namespace OrderFulfillment.Handlers;

/// <summary>
/// Resolves and invokes generated <see cref="INotificationHandler{T}"/>
/// instances for an event. Backed by an <see cref="IServiceProvider"/>
/// so the dependency on the saga handler types stays implicit.
/// </summary>
public interface INotificationPublisher
{
    Task PublishAsync<T>(T notification, CancellationToken ct) where T : INotification;
}

public sealed class ServiceProviderPublisher : INotificationPublisher
{
    private readonly IServiceProvider _sp;
    public ServiceProviderPublisher(IServiceProvider sp) => _sp = sp;

    public async Task PublishAsync<T>(T notification, CancellationToken ct) where T : INotification
    {
        // Resolve every registered handler for this event type and run them
        // sequentially. The saga registers exactly one handler per event;
        // additional sagas correlating on the same event would chain here.
        foreach (var handler in _sp.GetServices<INotificationHandler<T>>())
            await handler.Handle(notification, ct);
    }
}

/// <summary>
/// Captures a deferred publish so the caller can drain the queue once the
/// originating handler has released its per-saga lock. The boxed delegate
/// lets us retain the typed event without erasing it through reflection.
/// </summary>
internal sealed class DeferredPublishQueue
{
    private readonly ConcurrentQueue<Func<INotificationPublisher, CancellationToken, Task>> _queue = new();

    public void Enqueue<T>(T notification) where T : INotification
        => _queue.Enqueue((p, ct) => p.PublishAsync(notification, ct));

    /// <summary>Drains pending publishes serially using the supplied publisher;
    /// new entries enqueued during a publish are picked up in the same loop.</summary>
    public async Task DrainAsync(INotificationPublisher publisher, CancellationToken ct)
    {
        while (_queue.TryDequeue(out var publish))
            await publish(publisher, ct);
    }
}

/// <summary>
/// Demo state shared across the per-command handlers. Each handler:
///   1. Logs the command to the console so the demo flow is visible.
///   2. Queues the next-step notification on a deferred publish queue.
///      The queue is drained AFTER the current saga handler returns and
///      releases its per-saga lock — publishing synchronously from inside
///      Send would deadlock against the per-saga SemaphoreSlim.
/// </summary>
public sealed class FakeMediator
{
    /// <summary>
    /// Ambient slot read by the parameterless command handlers. Set once at app
    /// startup after the <see cref="IServiceProvider"/> resolves the singleton.
    /// </summary>
    public static FakeMediator? Current { get; set; }

    private readonly DeferredPublishQueue _deferred;
    private readonly ChargeReactionPolicy _chargeReaction;

    internal FakeMediator(DeferredPublishQueue deferred, ChargeReactionPolicy chargeReaction)
    {
        _deferred = deferred;
        _chargeReaction = chargeReaction;
    }

    /// <summary>
    /// When true, ReserveStock does NOT enqueue StockReserved.
    /// Lets a demo park the saga at Step 1 before invoking manual operations.
    /// </summary>
    public bool ParkAfterReserveStock { get; set; }

    internal void DispatchReserveStock(ReserveStockCommand req)
    {
        Console.WriteLine($"  [cmd] ReserveStock      {req.OrderId} total={req.Total:0.00}");
        if (!ParkAfterReserveStock)
            _deferred.Enqueue(new StockReserved(req.OrderId));
    }

    internal void DispatchChargeCustomer(ChargeCustomerCommand req)
    {
        Console.WriteLine($"  [cmd] ChargeCustomer    {req.OrderId} total={req.Total:0.00}");
        var reaction = _chargeReaction.Decide(req);
        switch (reaction)
        {
            case PaymentCharged pc: _deferred.Enqueue(pc); break;
            case PaymentDeclined pd: _deferred.Enqueue(pd); break;
            default: throw new InvalidOperationException($"Unhandled reaction type {reaction.GetType()}");
        }
    }
}

// Real IRequestHandlers — wired through the Mediator-generator-emitted dispatcher.
// Each handler delegates to the FakeMediator demo state which prints + enqueues the
// next notification. Handlers read the singleton FakeMediator from an ambient slot
// rather than ctor injection because the Mediator dispatcher's no-factory fallback
// path emits `new THandler()` and so requires a parameterless ctor on every handler.
internal sealed class ReserveStockCommandHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
    { FakeMediator.Current!.DispatchReserveStock(req); return new(Unit.Value); }
}

internal sealed class ChargeCustomerCommandHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public ValueTask<Unit> Handle(ChargeCustomerCommand req, CancellationToken ct)
    { FakeMediator.Current!.DispatchChargeCustomer(req); return new(Unit.Value); }
}

internal sealed class ShipOrderCommandHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
    {
        Console.WriteLine($"  [cmd] ShipOrder         {req.OrderId}");
        return new(Unit.Value);
    }
}

internal sealed class CancelReservationCommandHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public ValueTask<Unit> Handle(CancelReservationCommand req, CancellationToken ct)
    {
        Console.WriteLine($"  [cmp] CancelReservation {req.OrderId}");
        return new(Unit.Value);
    }
}

internal sealed class RefundPaymentCommandHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public ValueTask<Unit> Handle(RefundPaymentCommand req, CancellationToken ct)
    {
        Console.WriteLine($"  [cmp] RefundPayment     {req.OrderId}");
        return new(Unit.Value);
    }
}

/// <summary>
/// Mutable knob switching between "payment charge succeeds" (default) and
/// "payment charge declines" so the same demo binary can drive the happy
/// path and the compensation path in sequence.
/// </summary>
public sealed class ChargeReactionPolicy
{
    public bool DeclineNext { get; set; }
    public string DeclineReason { get; set; } = "insufficient funds";

    public INotification Decide(ChargeCustomerCommand req)
    {
        if (DeclineNext)
        {
            DeclineNext = false;
            return new PaymentDeclined(req.OrderId, DeclineReason);
        }
        return new PaymentCharged(req.OrderId);
    }
}

/// <summary>
/// Top-level driver: publishes the seed event, then drains the deferred
/// queue until the saga has nothing more to dispatch. Each iteration
/// happens OUTSIDE the per-saga lock because the previous handler has
/// already returned by the time we pop from the queue.
/// </summary>
public sealed class SagaDriver
{
    private readonly INotificationPublisher _publisher;
    private readonly DeferredPublishQueue _queue;

    internal SagaDriver(INotificationPublisher publisher, DeferredPublishQueue queue)
    {
        _publisher = publisher;
        _queue = queue;
    }

    public async Task PublishAndDrainAsync<T>(T seed, CancellationToken ct = default)
        where T : INotification
    {
        await _publisher.PublishAsync(seed, ct);
        await _queue.DrainAsync(_publisher, ct);
    }
}

/// <summary>
/// Wires the deferred queue + driver into DI alongside real IMediator (registered
/// by AddMediator()) so the generator-emitted Send dispatcher routes commands to
/// the per-command IRequestHandler implementations above.
/// </summary>
public static class FakeMediatorRegistration
{
    public static IServiceCollection AddFakeMediator(this IServiceCollection services)
    {
        services.AddMediator();

        services.AddSingleton<INotificationPublisher, ServiceProviderPublisher>();
        services.AddSingleton<ChargeReactionPolicy>();
        services.AddSingleton<DeferredPublishQueue>();
        services.AddSingleton<FakeMediator>(sp => new FakeMediator(
            sp.GetRequiredService<DeferredPublishQueue>(),
            sp.GetRequiredService<ChargeReactionPolicy>()));
        services.AddSingleton<SagaDriver>(sp => new SagaDriver(
            sp.GetRequiredService<INotificationPublisher>(),
            sp.GetRequiredService<DeferredPublishQueue>()));
        return services;
    }
}
