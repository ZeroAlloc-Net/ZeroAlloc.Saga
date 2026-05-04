using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga.Redis.Tests.Fixtures;

namespace ZeroAlloc.Saga.Redis.Tests;

/// <summary>
/// Full saga round-trip with the Redis backend: publish notifications, observe
/// per-step command dispatch via the ambient ledger, verify the saga is removed
/// from Redis after the terminal step.
/// </summary>
public sealed class E2ETests : IAsyncLifetime
{
    private readonly RedisFixture _fx = new();

    public Task InitializeAsync() => _fx.InitializeAsync();
    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    Task IAsyncLifetime.DisposeAsync() => _fx.DisposeAsync().AsTask();

    private IServiceProvider BuildHost()
    {
        SagaStoreRegistrar.Reset();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediator();
        services.AddSingleton(_fx.Multiplexer);
        services.AddSaga()
            .WithRedisStore(opts =>
            {
                opts.MaxRetryAttempts = 3;
                opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                opts.UseExponentialBackoff = false;
                opts.KeyPrefix = $"saga-e2e-{Guid.NewGuid():N}";
            })
            .WithOrderFulfillmentSaga();
        return services.BuildServiceProvider();
    }

    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        using var scope = sp.CreateScope();
        foreach (var h in scope.ServiceProvider.GetServices<INotificationHandler<T>>())
            await h.Handle(evt, default).ConfigureAwait(false);
    }

    [Fact]
    public async Task HappyPath_ReachesCompletedAndRemovesKey()
    {
        var sp = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        // Regression assertion: confirm the wired-up store is genuinely Redis-backed,
        // not the InMemorySagaStore default. Stage-2 tests previously passed for the
        // wrong reason — the generator's WithXxxSaga() emit invoked the registrar only
        // for the EfCore backend, leaving the InMemory fallback in place under
        // WithRedisStore. Failing this assertion would mean the saga was running in-process
        // and Assert.Empty(keys) below could pass without ever exercising Redis.
        using (var verifyScope = sp.CreateScope())
        {
            var resolvedStore = verifyScope.ServiceProvider
                .GetRequiredService<ISagaStore<OrderFulfillmentSaga, OrderId>>();
            Assert.IsType<RedisSagaStore<OrderFulfillmentSaga, OrderId>>(resolvedStore);
        }

        var keyPrefix = sp.GetRequiredService<RedisSagaStoreOptions>().KeyPrefix;
        var server = _fx.Multiplexer.GetServer(_fx.Multiplexer.GetEndPoints().Single());

        var orderId = new OrderId(1001);
        await PublishAsync(sp, new OrderPlaced(orderId, 250m));

        // Mid-flight: the saga is in step 1 of 3, must exist as a Redis Hash.
        var midKeys = server.Keys(pattern: $"{keyPrefix}:*").ToArray();
        Assert.NotEmpty(midKeys);

        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentCharged(orderId));

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
        Assert.Single(ledger.CommandsOfType<ChargeCustomerCommand>());
        Assert.Single(ledger.CommandsOfType<ShipOrderCommand>());
#pragma warning restore HLQ005

        // Saga removed from Redis after final step.
        var keys = server.Keys(pattern: $"{keyPrefix}:*").ToArray();
        Assert.Empty(keys);
    }

    [Fact]
    public async Task Compensation_TriggeredByPaymentDeclined()
    {
        var sp = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(2002);
        await PublishAsync(sp, new OrderPlaced(orderId, 50m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentDeclined(orderId));

        // Reverse cascade: refund first, then cancel.
        var refunds = ledger.CommandsOfType<RefundPaymentCommand>();
        var cancels = ledger.CommandsOfType<CancelReservationCommand>();
#pragma warning disable HLQ005
        Assert.Single(refunds);
        Assert.Single(cancels);
#pragma warning restore HLQ005

        var idxRefund = ledger.AllCommands.ToList().IndexOf(refunds[0]);
        var idxCancel = ledger.AllCommands.ToList().IndexOf(cancels[0]);
        Assert.True(idxRefund < idxCancel, "Refund must run before Cancel (reverse cascade).");
    }

    [Fact]
    public async Task State_PersistsAcrossSteps_RoundTripViaSnapshotRestore()
    {
        var sp = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(3003);
        await PublishAsync(sp, new OrderPlaced(orderId, 999.99m));
        await PublishAsync(sp, new StockReserved(orderId));

        var charges = ledger.CommandsOfType<ChargeCustomerCommand>();
#pragma warning disable HLQ005
        Assert.Single(charges);
#pragma warning restore HLQ005
        // The saga's Total field was set in Step 1 and survived a Save → Load round-trip
        // through the byte-encoded ISagaPersistableState in Redis.
        Assert.Equal(999.99m, charges[0].Total);
    }

    [Fact]
    public async Task FsmState_PersistsAcrossSteps_NotJustUserFields()
    {
        // Companion regression for the FSM-restore bug fixed alongside stage 3:
        // RedisSagaStore.Deserialize previously only rehydrated ISagaPersistableState's
        // Snapshot bytes, not CurrentFsmStateName. Without the fsmState field, the FSM
        // resets to Initial after every load, and Step 2's TryFire(StockReserved) fails
        // silently (returns false). This test exercises that the FSM transitions across
        // a save/load boundary by checking that step-2 actually fires a command.
        var sp = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;

        var orderId = new OrderId(4004);
        await PublishAsync(sp, new OrderPlaced(orderId, 1m));
        // After this point, the in-memory saga instance is gone (scoped store, scope
        // disposed). The next publish forces a Load from Redis, which must restore the
        // FSM state (step1 → step2) for ChargeCustomer to fire.
        await PublishAsync(sp, new StockReserved(orderId));

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ChargeCustomerCommand>());
#pragma warning restore HLQ005
    }
}
