using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Saga;

/// <summary>
/// DI registration entry point for the saga framework.
/// </summary>
public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the saga framework. v1.0 registers no per-saga types here —
    /// those come from generator-emitted <c>AddXxxSaga()</c> extensions for
    /// AOT-safety (no open-generic resolution at runtime).
    /// </summary>
    public static ISagaBuilder AddSaga(this IServiceCollection services)
        => new SagaBuilder(services);

    private sealed class SagaBuilder : ISagaBuilder
    {
        public IServiceCollection Services { get; }
        public SagaBuilder(IServiceCollection services) => Services = services;
    }
}
