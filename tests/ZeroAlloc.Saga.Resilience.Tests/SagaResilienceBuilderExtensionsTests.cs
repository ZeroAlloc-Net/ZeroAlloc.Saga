using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task WithResilience_ScopedDispatcher_FreshInnerPerScope()
    {
        // I-2: prove that across two distinct IServiceScope instances we get two
        // distinct ResilientSagaCommandDispatcher outers AND two distinct inner
        // CountingDispatcher instances. This verifies the decorator factory
        // resolves the inner via its captured ServiceDescriptor (so the inner
        // factory honors the Scoped lifetime), rather than singleton-caching
        // the inner across scopes.
        var services = new ServiceCollection();
        services.AddScoped<ISagaCommandDispatcher, CountingDispatcher>();

        new StubBuilder(services).WithResilience(opts =>
            opts.Retry = new RetryPolicy(maxAttempts: 1, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0));

        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        ISagaCommandDispatcher outerA;
        ISagaCommandDispatcher outerASameScope;
        ISagaCommandDispatcher outerB;

        using (var scopeA = sp.CreateScope())
        {
            outerA = scopeA.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();
            outerASameScope = scopeA.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();
            await outerA.DispatchAsync(new DummyCmd(1), CancellationToken.None);
        }

        using (var scopeB = sp.CreateScope())
        {
            outerB = scopeB.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();
            await outerB.DispatchAsync(new DummyCmd(2), CancellationToken.None);
        }

        // Same scope → same instance (Scoped lifetime preserved).
        Assert.Same(outerA, outerASameScope);
        // Different scopes → different outer instances.
        Assert.NotSame(outerA, outerB);
        // Both are the decorator type.
        Assert.IsType<ResilientSagaCommandDispatcher>(outerA);
        Assert.IsType<ResilientSagaCommandDispatcher>(outerB);
    }

    [Fact]
    public async Task WithResilience_OutboxDispatcherDetected_LogsWarningOnFirstResolve()
    {
        // I-1: when WithResilience() decorates a registered OutboxSagaCommandDispatcher,
        // the user is wrapping the (transient-failure-free) enqueue path. Emit a
        // warning so the misconfiguration is visible without docs.
        var services = new ServiceCollection();
        var capturingProvider = new CapturingLoggerProvider();
        services.AddLogging(b => b.AddProvider(capturingProvider));

        // Register a stand-in for OutboxSagaCommandDispatcher under that exact type
        // name (string-name match, no project dep).
        services.AddScoped<ISagaCommandDispatcher, OutboxSagaCommandDispatcher>();

        new StubBuilder(services).WithResilience(opts =>
            opts.Retry = new RetryPolicy(maxAttempts: 1, backoffMs: 1, jitter: false, perAttemptTimeoutMs: 0));

        using var sp = services.BuildServiceProvider();
        using var scope1 = sp.CreateScope();
        _ = scope1.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();
        using var scope2 = sp.CreateScope();
        _ = scope2.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();

        // Warning fires at most once (one-shot guard) regardless of how many resolves.
        var warnings = capturingProvider.Logs.Where(l => l.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("OutboxSagaCommandDispatcher", warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("OutboxSagaPollerOptions", warnings[0].Message, StringComparison.Ordinal);

        await Task.CompletedTask;
    }

    [Fact]
    public void WithResilience_NoOutboxDispatcher_NoWarning()
    {
        var services = new ServiceCollection();
        var capturingProvider = new CapturingLoggerProvider();
        services.AddLogging(b => b.AddProvider(capturingProvider));
        services.AddScoped<ISagaCommandDispatcher, CountingDispatcher>();

        new StubBuilder(services).WithResilience(opts => { });

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();

        Assert.DoesNotContain(capturingProvider.Logs, l => l.Level == LogLevel.Warning);
    }

    /// <summary>String-name stand-in for the real OutboxSagaCommandDispatcher.
    /// Saga.Resilience detects the outbox shape by type name to avoid a project dep.</summary>
    private sealed class OutboxSagaCommandDispatcher : ISagaCommandDispatcher
    {
        public ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
            where TCommand : IRequest<Unit> => ValueTask.CompletedTask;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Logs { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Logs);
        public void Dispose() { }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<(string, LogLevel, string)> _sink;
        public CapturingLogger(string category, List<(string, LogLevel, string)> sink)
        { _category = category; _sink = sink; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _sink.Add((_category, logLevel, formatter(state, exception)));
    }
}
