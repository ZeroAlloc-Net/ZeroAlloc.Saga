using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.Outbox.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Tests;

public class SagaOutboxRegistrationTests
{
    // The step and compensation commands of the fixture assembly's sagas, OrderFulfillmentSaga
    // and WelcomeSaga, as the outbox bridge writes them: Type.FullName, in ordinal order.
    private static readonly string[] FixtureCommandTypeNames =
    [
        typeof(CancelReservationCommand).FullName!,
        typeof(ChargeCustomerCommand).FullName!,
        typeof(RefundPaymentCommand).FullName!,
        typeof(ReserveStockCommand).FullName!,
        typeof(SendWelcomeCommand).FullName!,
        typeof(ShipOrderCommand).FullName!,
    ];

    [Fact]
    public void WithOutbox_ReplacesSagaCommandDispatcher()
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();
        // Pre-seed the default ISagaCommandDispatcher registration so Replace() has a target.
        // (The generator-emitted WithXxxSaga() normally registers MediatorSagaCommandDispatcher;
        // here we seed a sentinel to confirm WithOutbox swaps it.)
        services.AddScoped<ISagaCommandDispatcher, SentinelDispatcher>();

        builder.WithOutbox();

        var dispatcherDescriptor = services.Single(d => d.ServiceType == typeof(ISagaCommandDispatcher));
        Assert.Equal(typeof(OutboxSagaCommandDispatcher), dispatcherDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, dispatcherDescriptor.Lifetime);
    }

    [Fact]
    public void WithOutbox_DoesNotRegisterAnOutboxWorker()
    {
        // AddOutbox registers the worker with a plain AddHostedService, so a second call would
        // start a second worker. WithOutbox must leave it to the application.
        var services = new ServiceCollection();
        services.AddSaga().WithOutbox();

        Assert.DoesNotContain(services, d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxWorkerService));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithOutbox_RegistersOneScopedDispatcherPerSagaCommandType_InEitherOrder(bool outboxFirst)
    {
        var services = new ServiceCollection();
        SagaCommandRegistryDispatcher fake = (_, _, _, _) => default;
        services.AddSingleton(fake);

        if (outboxFirst)
            services.AddSaga().WithOutbox().WithOrderFulfillmentSaga();
        else
            services.AddSaga().WithOrderFulfillmentSaga().WithOutbox();

        var descriptors = services.Where(d => d.ServiceType == typeof(IOutboxTypeDispatcher)).ToList();
        Assert.Equal(FixtureCommandTypeNames.Length, descriptors.Count);
        Assert.All(descriptors, d => Assert.Equal(ServiceLifetime.Scoped, d.Lifetime));

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var names = scope.ServiceProvider.GetServices<IOutboxTypeDispatcher>()
            .Select(d => d.TypeName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(FixtureCommandTypeNames.Order(StringComparer.Ordinal), names, StringComparer.Ordinal);
    }

    [Fact]
    public void WithOutbox_CalledTwice_RegistersEachDispatcherOnce()
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();

        builder.WithOutbox().WithOrderFulfillmentSaga();
        builder.WithOutbox();

        Assert.Equal(
            FixtureCommandTypeNames.Length,
            services.Count(d => d.ServiceType == typeof(IOutboxTypeDispatcher)));
    }

    [Fact]
    public async Task SagaCommandDispatcher_DispatchesThroughTheDelegate_WithItsOwnScope()
    {
        var services = new ServiceCollection();
        var callCount = 0;
        string? calledTypeName = null;
        IServiceProvider? calledServices = null;
        SagaCommandRegistryDispatcher fake = (typeName, _, sp, _) =>
        {
            callCount++;
            calledTypeName = typeName;
            calledServices = sp;
            return default;
        };
        services.AddSingleton(fake);
        services.AddSaga().WithOutbox().WithOrderFulfillmentSaga();

        await using var root = services.BuildServiceProvider();
        await using var scope = root.CreateAsyncScope();
        var reserve = scope.ServiceProvider.GetServices<IOutboxTypeDispatcher>()
            .First(d => string.Equals(d.TypeName, typeof(ReserveStockCommand).FullName, StringComparison.Ordinal));

        await reserve.DispatchAsync(new byte[] { 1 }, CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.Equal(typeof(ReserveStockCommand).FullName, calledTypeName);
        // The worker resolves dispatchers and the store from one per-batch scope. A dispatcher
        // that dispatched through that same scope shares the store's DbContext.
        Assert.Same(scope.ServiceProvider, calledServices);
    }

    [Fact]
    public void WithOutbox_KeepsAPreRegisteredDispatcherDelegate()
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();
        services.AddScoped<ISagaCommandDispatcher, SentinelDispatcher>();

        // TryAddSingleton inside WithOutbox() must NOT overwrite this.
        SagaCommandRegistryDispatcher fake = (_, _, _, _) => default;
        services.AddSingleton(fake);

        builder.WithOutbox();

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<SagaCommandRegistryDispatcher>();
        Assert.Same(fake, resolved);
    }

    [Fact]
    public void WithOutbox_ReturnsBuilderForChaining()
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();
        services.AddScoped<ISagaCommandDispatcher, SentinelDispatcher>();

        var result = builder.WithOutbox();
        Assert.Same(builder, result);
    }

    private sealed class SentinelDispatcher : ISagaCommandDispatcher
    {
        public System.Threading.Tasks.ValueTask DispatchAsync<TCommand>(TCommand cmd, System.Threading.CancellationToken ct)
            where TCommand : ZeroAlloc.Mediator.IRequest<ZeroAlloc.Mediator.Unit>
            => default;
    }
}
