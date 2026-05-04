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

        var orderId = new OrderId(1001);
        await PublishAsync(sp, new OrderPlaced(orderId, 250m));
        await PublishAsync(sp, new StockReserved(orderId));
        await PublishAsync(sp, new PaymentCharged(orderId));

#pragma warning disable HLQ005
        Assert.Single(ledger.CommandsOfType<ReserveStockCommand>());
        Assert.Single(ledger.CommandsOfType<ChargeCustomerCommand>());
        Assert.Single(ledger.CommandsOfType<ShipOrderCommand>());
#pragma warning restore HLQ005

        // Saga removed from Redis after final step.
        var keyPrefix = sp.GetRequiredService<RedisSagaStoreOptions>().KeyPrefix;
        var keys = ((IDatabase)_fx.Multiplexer.GetDatabase())
            .Multiplexer.GetServer(_fx.Multiplexer.GetEndPoints().Single())
            .Keys(pattern: $"{keyPrefix}:*").ToArray();
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
}
