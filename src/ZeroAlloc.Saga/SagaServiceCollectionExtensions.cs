using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Saga;

/// <summary>
/// DI registration entry point for the saga framework.
/// </summary>
public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the saga framework and returns an <see cref="ISagaBuilder"/>
    /// for chaining backend selection (<c>WithEfCoreStore&lt;TContext&gt;</c>) and
    /// per-saga registrations (generator-emitted <c>WithXxxSaga()</c>).
    /// </summary>
    /// <remarks>
    /// v1.0 registered no per-saga types here — generator-emitted
    /// <c>WithXxxSaga()</c> extensions handle the AOT-safe closed-generic
    /// registrations. v1.1 keeps that contract; the only addition is the
    /// <see cref="ISagaBuilder.IsEfCoreBackend"/> flag, which backend packages
    /// flip via <see cref="ISagaBuilderMutable"/>. Mediator wiring is added
    /// implicitly by the generator-emitted <c>WithXxxSaga()</c> registration
    /// (<c>services.AddMediator()</c>) — users no longer need a separate
    /// <c>services.AddMediator()</c> call before <c>AddSaga()</c>.
    /// </remarks>
    public static ISagaBuilder AddSaga(this IServiceCollection services)
    {
        // Default retry options. Backends override via TryAddSingleton/replace
        // semantics so user-supplied tweaks (WithEfCoreStore configurator) win.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddSingleton(services, new SagaRetryOptions());
        return new SagaBuilder(services);
    }

    private sealed class SagaBuilder : ISagaBuilder, ISagaBuilderMutable
    {
        public IServiceCollection Services { get; }
        public bool IsEfCoreBackend { get; set; }
        public bool IsRedisBackend { get; set; }
        public SagaBuilder(IServiceCollection services) => Services = services;
    }
}
