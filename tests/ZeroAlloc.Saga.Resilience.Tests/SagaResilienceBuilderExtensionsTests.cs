using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Resilience;
using ZeroAlloc.Saga.Resilience;

namespace ZeroAlloc.Saga.Resilience.Tests;

public class SagaResilienceBuilderExtensionsTests
{
    public readonly record struct DummyCmd(int X) : IRequest<Unit>;

    public sealed class DummyHandler : IRequestHandler<DummyCmd, Unit>
    {
        public ValueTask<Unit> Handle(DummyCmd request, CancellationToken cancellationToken) => new(Unit.Value);
    }

    private sealed class StubBuilder : ISagaBuilder
    {
        public StubBuilder(IServiceCollection services) => Services = services;
        public IServiceCollection Services { get; }
        public bool IsEfCoreBackend => false;
    }

    private sealed class CountingDispatcher : ISagaCommandDispatcher
    {
        public int CallCount;
        public ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
            where TCommand : IRequest<Unit>
        {
            Interlocked.Increment(ref CallCount);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void WithResilience_NoExistingDispatcher_Throws()
    {
        var services = new ServiceCollection();
        var builder = new StubBuilder(services);

        var ex = Assert.Throws<InvalidOperationException>(
            () => builder.WithResilience(_ => { }));

        Assert.Contains("no ISagaCommandDispatcher", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithResilience_DecoratesScopedDispatcher_PreservesLifetime()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISagaCommandDispatcher, CountingDispatcher>();

        var builder = new StubBuilder(services);
        builder.WithResilience(opts => opts.Retry = new RetryPolicy(maxAttempts: 3, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0));

        // The decorator descriptor must keep the same Scoped lifetime as the
        // original — otherwise resolving it from a singleton context would
        // capture a scoped dependency.
        var descriptor = services.Single(d => d.ServiceType == typeof(ISagaCommandDispatcher));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = sp.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();
        Assert.IsType<ResilientSagaCommandDispatcher>(dispatcher);

        await dispatcher.DispatchAsync(new DummyCmd(1), CancellationToken.None);
    }

    [Fact]
    public void WithResilience_NullConfigure_Throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISagaCommandDispatcher, CountingDispatcher>();
        var builder = new StubBuilder(services);

        Assert.Throws<ArgumentNullException>(() => builder.WithResilience(null!));
    }

    [Fact]
    public void WithResilience_NullBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SagaResilienceBuilderExtensions.WithResilience(null!, _ => { }));
    }
}
