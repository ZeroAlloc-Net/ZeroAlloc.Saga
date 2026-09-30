using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Saga.Orm;
using ZeroAlloc.Saga.Outbox.Orm.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Orm.Tests;

/// <summary>
/// End-to-end tests for <c>WithOrmOutbox()</c> against a real SQLite database through the ORM
/// adapter, #197. A saga step's outbox rows commit in the saga store's transaction: both rows or
/// neither. ZeroAlloc.Outbox's worker then dispatches what committed.
/// </summary>
[Collection(SagaStoreRegistrarCollection.Name)]
public sealed class E2ETests : IAsyncLifetime
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(15);
    private readonly SqliteFixture _fx = new();

    public Task InitializeAsync() => _fx.MigrateAsync();

    public Task DisposeAsync() => _fx.DisposeAsync().AsTask();

    private IHost BuildHost(Action<IServiceCollection>? extra = null, int maxRetryAttempts = 3)
    {
        SagaStoreRegistrar.Reset();
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<DispatchedEventCounter>();
                services.AddSingleton<IOutboxDashboardEventPublisher>(
                    sp => sp.GetRequiredService<DispatchedEventCounter>());
                services.AddMediator();
                services.TryAddTransient<ReserveStockHandler>();
                services.TryAddTransient<ChargeCustomerHandler>();
                services.TryAddTransient<ShipOrderHandler>();
                services.TryAddTransient<CancelReservationHandler>();
                services.TryAddTransient<RefundPaymentHandler>();
                services.AddTestSerializers();
                services.AddWelcomeSagaFixture();
                // One connection per scope, as an application registers it. The saga store and
                // the outbox store of a scope share it.
                services.AddScoped(_ => _fx.Connection());
                services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50)).WithOrm();
                services.AddSaga()
                    .WithOrmStore(opts =>
                    {
                        opts.MaxRetryAttempts = maxRetryAttempts;
                        opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                        opts.UseExponentialBackoff = false;
                    })
                    .WithOutbox()
                    .WithOrmOutbox()
                    .WithOrderFulfillmentSaga()
                    .WithWelcomeSaga();
                extra?.Invoke(services);
            })
            .Build();
    }

    // IMediator.Publish has one overload per notification type, so each event needs its own helper.
    private static async Task PublishAsync(IServiceProvider sp, OrderPlaced evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, StockReserved evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, PaymentCharged evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, PaymentDeclined evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    private static async Task PublishAsync(IServiceProvider sp, CustomerRegistered evt)
    {
        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(evt, default).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the host, waits until the worker has dispatched <paramref name="expected"/> rows,
    /// and stops it. Set CommandLedger.Current first: the worker's execution context flows from
    /// StartAsync.
    /// </summary>
    private static async Task RunWorkerUntilDispatchedAsync(IHost host, int expected)
    {
        var counter = host.Services.GetRequiredService<DispatchedEventCounter>();
        await host.StartAsync().ConfigureAwait(false);
        try
        {
            var deadline = DateTime.UtcNow + WorkerTimeout;
            while (counter.Dispatched < expected)
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"The outbox worker did not finish within {WorkerTimeout.TotalSeconds} seconds.");
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Commit_Writes_The_Saga_Row_And_The_Outbox_Row_Together()
    {
        using var host = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;
        var orderId = new OrderId(7001);

        await PublishAsync(host.Services, new OrderPlaced(orderId, 199m));

        Assert.Equal(1, await _fx.CountSagasAsync());
        Assert.Equal([typeof(ReserveStockCommand).FullName!], await _fx.OutboxTypeNamesAsync());
        // The bridge enlists; nothing is dispatched inline.
        Assert.Empty(ledger.CommandsOfType<ReserveStockCommand>());

        await RunWorkerUntilDispatchedAsync(host, expected: 1);

        Assert.Equal(orderId, Assert.Single(ledger.CommandsOfType<ReserveStockCommand>()).OrderId);
    }

    [Fact]
    public async Task A_Conflict_On_Save_Discards_The_Outbox_Row_And_The_Retry_Commits_Exactly_One()
    {
        // The load-bearing case. After the step enlists its command and before the save, another
        // writer moves the saga's row version. The save's concurrency check fails, the store rolls
        // back, and the generated handler retries in a fresh scope. With the at-least-once default
        // the first attempt's row would already be committed, and the retry would add a second.
        var plan = new ConflictPlan();
        using var host = BuildHost(services => InjectConflicts(services, plan));
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;
        var orderId = new OrderId(7002);
        await PublishAsync(host.Services, new OrderPlaced(orderId, 42m));

        plan.Arm(conflicts: 1, orderId.ToString());
        await PublishAsync(host.Services, new StockReserved(orderId));

        Assert.Equal(1, plan.Injected);
        Assert.Equal(
            [typeof(ChargeCustomerCommand).FullName!, typeof(ReserveStockCommand).FullName!],
            await _fx.OutboxTypeNamesAsync());

        await RunWorkerUntilDispatchedAsync(host, expected: 2);

        Assert.Single(ledger.CommandsOfType<ChargeCustomerCommand>());
    }

    [Fact]
    public async Task A_Save_That_Keeps_Conflicting_Commits_No_Outbox_Row()
    {
        var plan = new ConflictPlan();
        using var host = BuildHost(services => InjectConflicts(services, plan), maxRetryAttempts: 2);
        var orderId = new OrderId(7003);
        await PublishAsync(host.Services, new OrderPlaced(orderId, 42m));

        plan.Arm(conflicts: int.MaxValue, orderId.ToString());
        // The retry loop gives up with its own exception, carrying the store's last conflict.
        var ex = await Assert.ThrowsAsync<SagaConcurrencyException>(
            () => PublishAsync(host.Services, new StockReserved(orderId)));

        Assert.IsType<OrmSagaConcurrencyException>(ex.InnerException);
        Assert.True(plan.Injected >= 2, $"expected every attempt to conflict, got {plan.Injected}");
        Assert.Equal([typeof(ReserveStockCommand).FullName!], await _fx.OutboxTypeNamesAsync());
    }

    [Fact]
    public async Task A_Removal_That_Keeps_Conflicting_Commits_No_Outbox_Row_And_Keeps_The_Saga()
    {
        // The last step ends in RemoveAsync. Its versioned delete fails the same way, and its
        // command must be discarded with it.
        var plan = new ConflictPlan();
        using var host = BuildHost(services => InjectConflicts(services, plan), maxRetryAttempts: 2);
        var orderId = new OrderId(7004);
        await PublishAsync(host.Services, new OrderPlaced(orderId, 5m));
        await PublishAsync(host.Services, new StockReserved(orderId));

        plan.Arm(conflicts: int.MaxValue, orderId.ToString());
        var ex = await Assert.ThrowsAsync<SagaConcurrencyException>(
            () => PublishAsync(host.Services, new PaymentCharged(orderId)));

        Assert.IsType<OrmSagaConcurrencyException>(ex.InnerException);

        Assert.Equal(1, await _fx.CountSagasAsync());
        Assert.DoesNotContain(typeof(ShipOrderCommand).FullName!, await _fx.OutboxTypeNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_Failed_Outbox_Write_Rolls_Back_The_Saga_Row()
    {
        // Any failure inside the transaction discards the saga row as well, so the saga never
        // records a step whose command was not recorded. Here the outbox table does not exist.
        await _fx.ExecuteAsync("DROP TABLE OutboxMessages");
        using var host = BuildHost();
        var orderId = new OrderId(7005);

        await Assert.ThrowsAsync<SqliteException>(
            () => PublishAsync(host.Services, new OrderPlaced(orderId, 1m)));

        Assert.Equal(0, await _fx.CountSagasAsync());
    }

    [Fact]
    public async Task SingleStepSaga_StartedAndCompletedByOneEvent_CommitsItsOutboxRow()
    {
        // #194: the handler ends in RemoveAsync with no saga row, and the enlisted row must still
        // commit.
        using var host = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;
        var customerId = new CustomerId(8001);

        await PublishAsync(host.Services, new CustomerRegistered(customerId));

        Assert.Equal([typeof(SendWelcomeCommand).FullName!], await _fx.OutboxTypeNamesAsync());
        Assert.Equal(0, await _fx.CountSagasAsync());

        await RunWorkerUntilDispatchedAsync(host, expected: 1);

        Assert.Equal(customerId, Assert.Single(ledger.CommandsOfType<SendWelcomeCommand>()).CustomerId);
    }

    [Fact]
    public async Task LastStep_OfAMultiStepSaga_CommitsItsOutboxRow_AndRemovesTheSaga()
    {
        using var host = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;
        var orderId = new OrderId(8101);

        await PublishAsync(host.Services, new OrderPlaced(orderId, 5m));
        await PublishAsync(host.Services, new StockReserved(orderId));
        await PublishAsync(host.Services, new PaymentCharged(orderId));

        Assert.Equal(
            [typeof(ChargeCustomerCommand).FullName!, typeof(ReserveStockCommand).FullName!, typeof(ShipOrderCommand).FullName!],
            await _fx.OutboxTypeNamesAsync());
        Assert.Equal(0, await _fx.CountSagasAsync());

        await RunWorkerUntilDispatchedAsync(host, expected: 3);

        Assert.Equal(orderId, Assert.Single(ledger.CommandsOfType<ShipOrderCommand>()).OrderId);
    }

    [Fact]
    public async Task Compensation_CommitsItsCompensationCommands_AndRemovesTheSaga()
    {
        using var host = BuildHost();
        var ledger = new CommandLedger();
        CommandLedger.Current = ledger;
        var orderId = new OrderId(8201);

        await PublishAsync(host.Services, new OrderPlaced(orderId, 5m));
        await PublishAsync(host.Services, new StockReserved(orderId));
        await PublishAsync(host.Services, new PaymentDeclined(orderId));

        var names = await _fx.OutboxTypeNamesAsync();
        Assert.Contains(typeof(RefundPaymentCommand).FullName!, names, StringComparer.Ordinal);
        Assert.Contains(typeof(CancelReservationCommand).FullName!, names, StringComparer.Ordinal);
        Assert.Equal(0, await _fx.CountSagasAsync());

        await RunWorkerUntilDispatchedAsync(host, expected: names.Length);

        Assert.Single(ledger.CommandsOfType<RefundPaymentCommand>());
        Assert.Single(ledger.CommandsOfType<CancelReservationCommand>());
    }

    [Fact]
    public async Task Start_With_OrmStore_WithOrmOutbox_And_The_Orm_Outbox_Store_Succeeds()
    {
        using var host = BuildHost();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Start_With_Another_Outbox_Store_Than_The_Orm_One_Fails()
    {
        // The rows are written through OrmOutboxStore, so a worker claiming from any other store
        // would never dispatch a saga command.
        using var host = BuildHost(services =>
        {
            services.RemoveAll<IOutboxStore>();
            services.AddScoped<IOutboxStore, StrayOutboxStore>();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.StartsWith("ZeroAlloc.Saga.Outbox.Orm.WithOrmOutbox(): ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("StrayOutboxStore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_The_Orm_Outbox_Store_A_Save_With_Commands_Fails_And_Commits_Nothing()
    {
        // A container that is never started as a host skips the startup check. The contributor
        // checks again, inside the transaction, and the saga row rolls back with it.
        using var host = BuildHost(services =>
        {
            services.RemoveAll<IOutboxStore>();
            services.AddScoped<IOutboxStore, StrayOutboxStore>();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PublishAsync(host.Services, new OrderPlaced(new OrderId(7006), 1m)));

        Assert.Contains("StrayOutboxStore", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _fx.CountSagasAsync());
    }

    private void InjectConflicts(IServiceCollection services, ConflictPlan plan)
    {
        // Wraps the unit of work WithOrmOutbox() registered, so the rows still reach its buffer.
        var original = services.Last(d => d.ServiceType == typeof(ISagaUnitOfWork));
        services.Replace(ServiceDescriptor.Scoped<ISagaUnitOfWork>(sp =>
        {
            var inner = original.ImplementationFactory is { } factory
                ? factory(sp)
                : ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
            return new ConflictInjector((ISagaUnitOfWork)inner, _fx, plan);
        }));
    }

    private sealed class ConflictPlan
    {
        private int _remaining;
        private int _injected;
        private string _correlationKey = "";

        public int Injected => Volatile.Read(ref _injected);

        public string CorrelationKey => Volatile.Read(ref _correlationKey);

        public void Arm(int conflicts, string correlationKey)
        {
            Volatile.Write(ref _correlationKey, correlationKey);
            Volatile.Write(ref _remaining, conflicts);
        }

        public bool TryConsume()
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _remaining);
                if (remaining <= 0)
                    return false;
                if (Interlocked.CompareExchange(ref _remaining, remaining - 1, remaining) == remaining)
                {
                    Interlocked.Increment(ref _injected);
                    return true;
                }
            }
        }
    }

    // Enlists into the real unit of work, then, while the plan lasts, moves the saga row's version
    // as a concurrent writer would. The step has already loaded the saga, so its save or removal
    // then fails the concurrency check.
    private sealed class ConflictInjector(ISagaUnitOfWork inner, SqliteFixture fx, ConflictPlan plan) : ISagaUnitOfWork
    {
        public async ValueTask EnlistOutboxRowAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            await inner.EnlistOutboxRowAsync(typeName, payload, ct).ConfigureAwait(false);
            if (plan.TryConsume())
                await fx.ChangeRowVersionBehindTheStoreAsync(plan.CorrelationKey).ConfigureAwait(false);
        }
    }

    // Stands in for any other IOutboxStore.
    private sealed class StrayOutboxStore : IOutboxStore
    {
        public ValueTask EnqueueAsync(string typeName, ReadOnlyMemory<byte> payload, System.Data.Common.DbTransaction? transaction, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<System.Collections.Generic.IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> RenewLeaseAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<int> ReleaseLeasesAsync(System.Collections.Generic.IReadOnlyList<OutboxMessageId> ids, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkSucceededAsync(OutboxMessageId id, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> MarkFailedAsync(OutboxMessageId id, int retryCount, DateTimeOffset nextRetryAt, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<bool> DeadLetterAsync(OutboxMessageId id, string error, OutboxLease lease, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
