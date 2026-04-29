using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.Tests.Fixtures;

namespace ZeroAlloc.Saga.Tests;

/// <summary>
/// End-to-end runtime tests for the saga framework. Tests publish events by
/// resolving the generated <see cref="INotificationHandler{TEvent}"/> from DI
/// and invoking <c>Handle</c> directly. Command dispatch goes through a
/// <see cref="RecordingMediator"/> that records each <c>Send</c> call.
/// </summary>
public class RuntimeTests
{
    private static (IServiceProvider Sp, RecordingMediator Mediator) BuildHost(
        Action<IServiceCollection>? configure = null,
        Func<object, Task>? onSend = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IMediator>(_ => new RecordingMediator(onSend));
        services.AddSaga()
            .AddOrderFulfillmentSaga();
        configure?.Invoke(services);
        var sp = services.BuildServiceProvider();
        var mediator = (RecordingMediator)sp.GetRequiredService<IMediator>();
        return (sp, mediator);
    }

    private static Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        var handlers = sp.GetServices<INotificationHandler<T>>();
        var task = Task.CompletedTask;
        foreach (var h in handlers)
        {
            var current = h.Handle(evt, default);
            task = task.ContinueWith(_ => current.AsTask(), TaskScheduler.Default).Unwrap();
        }
        return task;
    }

    [Fact]
    public async Task Forward_HappyPath()
    {
        var (sp, mediator) = BuildHost();
        var orderId = new OrderId(1);

        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentCharged(orderId));

        Assert.Single(mediator.CommandsOfType<ReserveStockCommand>());
        Assert.Single(mediator.CommandsOfType<ChargeCustomerCommand>());
        Assert.Single(mediator.CommandsOfType<ShipOrderCommand>());

        // Saga removed after final step.
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        Assert.Null(await manager.GetAsync(orderId, default));
    }

    [Fact]
    public async Task Forward_AutoCreates_OnStep1Event()
    {
        var (sp, _) = BuildHost();
        var orderId = new OrderId(2);

        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        Assert.Null(await manager.GetAsync(orderId, default));

        await PublishAsync(sp, new OrderPlaced(orderId, 50m));

        var saga = await manager.GetAsync(orderId, default);
        Assert.NotNull(saga);
        Assert.Equal(orderId, saga!.OrderId);
        Assert.Equal(50m, saga.Total);
    }

    [Fact]
    public async Task Forward_LateEvent_SkipsSilently()
    {
        var (sp, mediator) = BuildHost();
        var orderId = new OrderId(3);

        // No prior OrderPlaced — fire StockReserved out of order.
        await PublishAsync(sp, new StockReserved(orderId));

        Assert.Empty(mediator.AllCommands);
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        // Saga is auto-created (LoadOrCreate) but its FSM stayed in NotStarted, so the
        // event was rejected by TryFire and no command dispatched. The instance still exists.
        var saga = await manager.GetAsync(orderId, default);
        Assert.NotNull(saga);
        Assert.Equal(OrderFulfillmentSagaFsm.State.NotStarted, saga!.Fsm.Current);
    }

    [Fact]
    public async Task Forward_DuplicateStep1Event_IsNoOp()
    {
        var (sp, mediator) = BuildHost();
        var orderId = new OrderId(4);

        await PublishAsync(sp, new OrderPlaced(orderId, 25m));
        await PublishAsync(sp, new OrderPlaced(orderId, 25m));

        Assert.Single(mediator.CommandsOfType<ReserveStockCommand>());
    }

    [Fact]
    public async Task Compensation_TriggeredOnFailureEvent()
    {
        var (sp, mediator) = BuildHost();
        var orderId = new OrderId(5);

        await PublishAsync(sp, new OrderPlaced(orderId, 75m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentDeclined(orderId));

        // Reverse cascade: Refund first, then CancelReservation.
        var refunds = mediator.CommandsOfType<RefundPaymentCommand>();
        var cancels = mediator.CommandsOfType<CancelReservationCommand>();
        Assert.Single(refunds);
        Assert.Single(cancels);

        // Order: refund recorded before cancel.
        var idxRefund = ((List<object>)mediator.AllCommands.ToList()).IndexOf(refunds[0]);
        var idxCancel = ((List<object>)mediator.AllCommands.ToList()).IndexOf(cancels[0]);
        Assert.True(idxRefund < idxCancel, "Refund should be dispatched before CancelReservation");

        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        Assert.Null(await manager.GetAsync(orderId, default));
    }

    [Fact]
    public async Task Compensation_OrphanFailureEvent_NoCommandsDispatched()
    {
        var (sp, mediator) = BuildHost();
        var orphanId = new OrderId(6);

        // No prior saga — orphan PaymentDeclined.
        await PublishAsync(sp, new PaymentDeclined(orphanId));

        Assert.Empty(mediator.AllCommands);
        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        Assert.Null(await manager.GetAsync(orphanId, default));
    }

    [Fact]
    public async Task Compensation_Manual_ViaSagaManager()
    {
        var (sp, mediator) = BuildHost();
        var orderId = new OrderId(7);

        await PublishAsync(sp, new OrderPlaced(orderId, 200m));
        await PublishAsync(sp, new StockReserved(orderId));

        var manager = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        await manager.CompensateAsync(orderId, default);

        Assert.Single(mediator.CommandsOfType<RefundPaymentCommand>());
        Assert.Single(mediator.CommandsOfType<CancelReservationCommand>());

        Assert.Null(await manager.GetAsync(orderId, default));
    }

    [Fact]
    public async Task Concurrency_SerializesPerSaga()
    {
        // Use a barrier in the mediator to detect overlap on the same saga.
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new System.Threading.Lock();

        var (sp, _) = BuildHost(onSend: async cmd =>
        {
            int now;
            lock (gate)
            {
                inFlight++;
                now = inFlight;
                if (now > maxInFlight) maxInFlight = now;
            }
            await Task.Delay(20).ConfigureAwait(false);
            lock (gate) inFlight--;
        });

        var orderId = new OrderId(8);
        // Pump OrderPlaced and StockReserved in parallel.
        var t1 = PublishAsync(sp, new OrderPlaced(orderId, 1m));
        var t2 = PublishAsync(sp, new StockReserved(orderId));
        await Task.WhenAll(t1, t2);

        Assert.True(maxInFlight <= 1, $"Expected serial execution; observed {maxInFlight} concurrent commands");
    }

    [Fact]
    public async Task Concurrency_DifferentSagas_RunInParallel()
    {
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new System.Threading.Lock();
        var ready = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var (sp, _) = BuildHost(onSend: async cmd =>
        {
            int now;
            lock (gate)
            {
                inFlight++;
                now = inFlight;
                if (now > maxInFlight) maxInFlight = now;
            }

            if (now == 1)
            {
                ready.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            else
            {
                release.TrySetResult();
            }
            lock (gate) inFlight--;
        });

        var t1 = PublishAsync(sp, new OrderPlaced(new OrderId(9), 1m));
        var t2 = Task.Run(async () =>
        {
            await ready.Task.ConfigureAwait(false);
            await PublishAsync(sp, new OrderPlaced(new OrderId(10), 2m)).ConfigureAwait(false);
        });

        await Task.WhenAll(t1, t2);
        Assert.True(maxInFlight >= 2, $"Expected parallel execution across sagas; observed max {maxInFlight}");
    }

    [Fact]
    public async Task Multiple_Sagas_Same_Event()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<IMediator>(_ => new RecordingMediator());
        services.AddSaga()
            .AddOrderFulfillmentSaga()
            .AddRefundSaga();
        var sp = services.BuildServiceProvider();
        var mediator = (RecordingMediator)sp.GetRequiredService<IMediator>();
        var orderId = new OrderId(11);

        // Both sagas correlate on OrderPlaced.
        await PublishAsync(sp, new OrderPlaced(orderId, 10m));

        Assert.Single(mediator.CommandsOfType<ReserveStockCommand>());
        Assert.Single(mediator.CommandsOfType<AuditOrderCommand>());

        var fulfillmentMgr = sp.GetRequiredService<ISagaManager<OrderFulfillmentSaga, OrderId>>();
        var refundMgr = sp.GetRequiredService<ISagaManager<RefundSaga, OrderId>>();
        Assert.NotNull(await fulfillmentMgr.GetAsync(orderId, default));
        Assert.Null(await refundMgr.GetAsync(orderId, default)); // single-step saga removes itself
    }

    [Fact]
    public async Task State_PersistsAcrossSteps()
    {
        var (sp, mediator) = BuildHost();
        var orderId = new OrderId(12);
        const decimal total = 999.99m;

        await PublishAsync(sp, new OrderPlaced(orderId, total));
        await PublishAsync(sp, new StockReserved(orderId));

        var charge = mediator.CommandsOfType<ChargeCustomerCommand>();
        Assert.Single(charge);
        Assert.Equal(total, charge[0].Total);
    }
}
