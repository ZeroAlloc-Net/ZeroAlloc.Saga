using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga.Tests.Fixtures;
using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Saga.Tests;

/// <summary>
/// The log calls the generator emits into each saga handler, #248. A disabled level must cost
/// nothing: the late-event and orphan paths run on every duplicate or out-of-order delivery, so
/// a params-array log call there allocated an object[] and boxed the key on each one even when
/// nobody was listening. An enabled level must still produce the same entry as before: same
/// level, event id, message template and structured values.
/// </summary>
public class GeneratedHandlerLoggingTests
{
    private const int Iterations = 1_000;

    private static ServiceProvider BuildHost(Action<ILoggingBuilder> logging)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging);
        services.AddMediator();
        services.TryAddTransient<ReserveStockHandler>();
        services.TryAddTransient<ChargeCustomerHandler>();
        services.TryAddTransient<ShipOrderHandler>();
        services.TryAddTransient<CancelReservationHandler>();
        services.TryAddTransient<RefundPaymentHandler>();
        services.TryAddTransient<AuditOrderHandler>();
        services.AddSaga().WithOrderFulfillmentSaga();
        CommandLedger.Current = new CommandLedger();
        return services.BuildServiceProvider();
    }

    // Every log level disabled, as in a production host whose minimum level is above Debug and
    // whose providers filter the saga categories out.
    private static ServiceProvider BuildQuietHost()
        => BuildHost(b => b.AddProvider(NullLoggerProvider.Instance));

    private static void RunSync(ValueTask handle)
    {
        if (!handle.IsCompletedSuccessfully)
            throw new InvalidOperationException("Handle must complete synchronously for the measurement.");
        handle.GetAwaiter().GetResult();
    }

    private static T RunSync<T>(ValueTask<T> pending)
    {
        if (!pending.IsCompletedSuccessfully)
            throw new InvalidOperationException("The call must complete synchronously for the measurement.");
        return pending.Result;
    }

    /// <summary>
    /// The work every handler attempt does before it decides to log: take the key lock, open the
    /// attempt scope, resolve the store and dispatcher. The handler path under test may allocate
    /// this much and nothing more.
    /// </summary>
    private static OrderFulfillmentSaga? AttemptPreamble(IServiceProvider sp, OrderId key, bool create)
    {
        var locks = sp.GetRequiredService<SagaLockManager<OrderId>>();
        using var held = RunSync(locks.AcquireAsync(key, default));
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();
        _ = scope.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();
        return create
            ? RunSync(store.LoadOrCreateAsync(key, default))
            : RunSync(store.TryLoadAsync(key, default));
    }

    [Fact]
    public async Task Late_Forward_Event_With_Debug_Disabled_Allocates_Nothing_Beyond_The_Attempt()
    {
        await using var sp = BuildQuietHost();
        var key = new OrderId(1);
        var evt = new OrderPlaced(key, 10m);
        await sp.GetRequiredService<IMediator>().Publish(evt, default);

        // The saga is in Step1, so a second OrderPlaced is a duplicate: TryFire fails and the
        // handler logs at Debug and returns.
        var handler = sp.GetRequiredService<INotificationHandler<OrderPlaced>>();

        AllocationGate.AssertNoMoreThan(
            Iterations,
            baseline: () => _ = AttemptPreamble(sp, key, create: true)!.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.OrderPlaced),
            candidate: () => RunSync(handler.Handle(evt, default)),
            label: "late forward event, Debug disabled");
    }

    [Fact]
    public void Orphan_Compensation_Event_With_Warning_Disabled_Allocates_Nothing_Beyond_The_Attempt()
    {
        using var sp = BuildQuietHost();
        var key = new OrderId(2);
        var evt = new PaymentDeclined(key);
        var handler = sp.GetRequiredService<INotificationHandler<PaymentDeclined>>();

        AllocationGate.AssertNoMoreThan(
            Iterations,
            baseline: () => _ = AttemptPreamble(sp, key, create: false),
            candidate: () => RunSync(handler.Handle(evt, default)),
            label: "orphan compensation event, Warning disabled");
    }

    [Fact]
    public async Task Compensation_Event_In_Wrong_State_With_Debug_Disabled_Allocates_Nothing_Beyond_The_Attempt()
    {
        await using var sp = BuildQuietHost();
        var key = new OrderId(3);
        await sp.GetRequiredService<IMediator>().Publish(new OrderPlaced(key, 10m), default);

        // PaymentDeclined compensates Step2; in Step1 TryFire fails and the handler logs at Debug.
        var evt = new PaymentDeclined(key);
        var handler = sp.GetRequiredService<INotificationHandler<PaymentDeclined>>();

        AllocationGate.AssertNoMoreThan(
            Iterations,
            baseline: () =>
            {
                var saga = AttemptPreamble(sp, key, create: false)!;
                _ = saga.Fsm.Current;
                _ = saga.Fsm.TryFire(OrderFulfillmentSagaFsm.Trigger.PaymentDeclined);
            },
            candidate: () => RunSync(handler.Handle(evt, default)),
            label: "compensation event in wrong state, Debug disabled");
    }

    [Fact]
    public async Task Late_Forward_Event_Logs_The_Same_Entry_As_Before()
    {
        var sink = new CapturingLoggerProvider();
        await using var sp = BuildHost(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        var key = new OrderId(4);
        await sp.GetRequiredService<IMediator>().Publish(new OrderPlaced(key, 10m), default);
        sink.Entries.Clear();

        await sp.GetRequiredService<IMediator>().Publish(new OrderPlaced(key, 10m), default);

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal(new EventId(0), entry.EventId);
        Assert.Null(entry.EventId.Name);
        Assert.Null(entry.Exception);
        Assert.EndsWith("OrderFulfillmentSaga_OrderPlaced_Handler", entry.Category, StringComparison.Ordinal);
        Assert.Equal("Saga OrderFulfillmentSaga: late OrderPlaced for key OrderId { V = 4 }; ignored", entry.Message);
        Assert.Equal(
            [
                new("Saga", "OrderFulfillmentSaga"),
                new("Key", key),
                new("{OriginalFormat}", "Saga {Saga}: late OrderPlaced for key {Key}; ignored"),
            ],
            entry.State);
    }

    [Fact]
    public async Task Orphan_Compensation_Event_Logs_The_Same_Entry_As_Before()
    {
        var sink = new CapturingLoggerProvider();
        await using var sp = BuildHost(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        var key = new OrderId(5);

        await sp.GetRequiredService<IMediator>().Publish(new PaymentDeclined(key), default);

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(new EventId(0), entry.EventId);
        Assert.Null(entry.EventId.Name);
        Assert.Equal("Saga OrderFulfillmentSaga: orphan PaymentDeclined for key OrderId { V = 5 }; no instance to compensate", entry.Message);
        Assert.Equal(
            [
                new("Saga", "OrderFulfillmentSaga"),
                new("Key", key),
                new("{OriginalFormat}", "Saga {Saga}: orphan PaymentDeclined for key {Key}; no instance to compensate"),
            ],
            entry.State);
    }

    [Fact]
    public async Task Compensation_Event_In_Wrong_State_Logs_The_Same_Entry_As_Before()
    {
        var sink = new CapturingLoggerProvider();
        await using var sp = BuildHost(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        var key = new OrderId(6);
        await sp.GetRequiredService<IMediator>().Publish(new OrderPlaced(key, 10m), default);
        sink.Entries.Clear();

        await sp.GetRequiredService<IMediator>().Publish(new PaymentDeclined(key), default);

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal(new EventId(0), entry.EventId);
        Assert.Null(entry.EventId.Name);
        Assert.Equal("Saga OrderFulfillmentSaga: PaymentDeclined not valid in state Step1 for key OrderId { V = 6 }; ignored", entry.Message);
        Assert.Equal(
            [
                new("Saga", "OrderFulfillmentSaga"),
                new("State", OrderFulfillmentSagaFsm.State.Step1),
                new("Key", key),
                new("{OriginalFormat}", "Saga {Saga}: PaymentDeclined not valid in state {State} for key {Key}; ignored"),
            ],
            entry.State);
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        List<KeyValuePair<string, object?>> State);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, List<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToList() : [];
                lock (entries)
                    entries.Add(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception, values));
            }
        }
    }
}
