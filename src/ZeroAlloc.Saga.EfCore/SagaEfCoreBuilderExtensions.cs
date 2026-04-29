using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.Saga.EfCore;

/// <summary>
/// <see cref="ISagaBuilder"/> extensions that wire the EF Core durable
/// backend onto a saga registration pipeline.
/// </summary>
public static class SagaEfCoreBuilderExtensions
{
    /// <summary>
    /// Configures the saga framework to persist instances via the supplied
    /// <typeparamref name="TContext"/>. Flips
    /// <see cref="ISagaBuilder.IsEfCoreBackend"/> so generator-emitted
    /// <c>AddXxxSaga()</c> registrations select <c>EfCoreSagaStore&lt;,&gt;</c>
    /// instead of the in-memory default.
    /// </summary>
    /// <typeparam name="TContext">The user's <see cref="DbContext"/> type.</typeparam>
    /// <param name="builder">The saga builder.</param>
    /// <param name="configure">Optional <see cref="EfCoreSagaStoreOptions"/> tweaks.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// PR 2 only sets the backend flag and registers options. The actual
    /// <c>EfCoreSagaStore&lt;,&gt;</c> registration plus per-saga wiring
    /// lands in PR 3.
    /// </remarks>
    public static ISagaBuilder WithEfCoreStore<TContext>(
        this ISagaBuilder builder,
        Action<EfCoreSagaStoreOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.SetEfCoreBackend();

        var options = new EfCoreSagaStoreOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        // Real DbContext + EfCoreSagaStore<,> registration lands in PR 3.
        return builder;
    }
}
