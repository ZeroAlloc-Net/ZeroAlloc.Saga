using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Outbox.Tests;

public class SagaOutboxRegistrationTests
{
    [Fact]
    public void WithOutbox_ReplacesSagaCommandDispatcher()
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();
        // Pre-seed the default ISagaCommandDispatcher registration so Replace() has a target.
        // (The generator-emitted AddXxxSaga() normally registers MediatorSagaCommandDispatcher;
        // here we seed a sentinel to confirm WithOutbox swaps it.)
        services.AddScoped<ISagaCommandDispatcher, SentinelDispatcher>();

        builder.WithOutbox();

        var dispatcherDescriptor = services.Single(d => d.ServiceType == typeof(ISagaCommandDispatcher));
        Assert.Equal(typeof(OutboxSagaCommandDispatcher), dispatcherDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, dispatcherDescriptor.Lifetime);
    }

    [Fact]
    public void WithOutbox_RegistersHostedService()
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();
        services.AddScoped<ISagaCommandDispatcher, SentinelDispatcher>();

        builder.WithOutbox();

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        Assert.Contains(hosted, d => d.ImplementationType == typeof(OutboxSagaCommandPoller));
    }

    [Fact]
    public void WithOutbox_RegistersDispatcherDelegate_Lazily()
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();
        services.AddScoped<ISagaCommandDispatcher, SentinelDispatcher>();

        // Pre-register a fake delegate to short-circuit the reflective lookup.
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
