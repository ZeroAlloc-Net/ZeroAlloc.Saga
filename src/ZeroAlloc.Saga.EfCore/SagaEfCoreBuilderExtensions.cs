using System;
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
    /// <c>AddXxxSaga()</c> registrations select <see cref="EfCoreSagaStore{TSaga,TKey}"/>
    /// instead of the in-memory default.
    /// </summary>
    /// <typeparam name="TContext">The user's <see cref="DbContext"/> type.</typeparam>
    /// <param name="builder">The saga builder.</param>
    /// <param name="configure">Optional <see cref="EfCoreSagaStoreOptions"/> tweaks.</param>
    /// <returns>The same builder for chaining.</returns>
    /// <remarks>
    /// Must be called BEFORE per-saga <c>AddXxxSaga()</c> registrations so
    /// the generator-emitted code can dispatch to the EF Core registrar
    /// rather than installing the in-memory default.
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

        // Replace the default SagaRetryOptions registered by AddSaga() with
        // the EfCore-specific instance so generator-emitted handlers (which
        // inject SagaRetryOptions) see the user-supplied retry tunables.
        for (int i = builder.Services.Count - 1; i >= 0; i--)
        {
            if (builder.Services[i].ServiceType == typeof(SagaRetryOptions))
            {
                builder.Services.RemoveAt(i);
            }
        }
        builder.Services.AddSingleton<SagaRetryOptions>(options);

        // Map DbContext -> TContext so EfCoreSagaStore<TSaga, TKey> can take a
        // DbContext base-class dependency without binding to TContext at the
        // generic level. Single-context limitation (BACKLOG #21 lifts in v1.x).
        builder.Services.TryAddScoped<DbContext>(sp => sp.GetRequiredService<TContext>());

        // Install a typed registrar so generator-emitted Apply<TSaga,TKey>
        // dispatches to EfCore's per-saga registration with full type info —
        // closed-generic, AOT-safe, no MakeGenericType.
        SagaStoreRegistrar.SetTypedRegistrar(EfCoreSagaStoreRegistrar.Instance);

        return builder;
    }

    private sealed class EfCoreSagaStoreRegistrar : ISagaStoreRegistrar
    {
        public static readonly EfCoreSagaStoreRegistrar Instance = new();

        public void Register<TSaga, TKey>(ISagaBuilder builder)
            where TSaga : class, new()
            where TKey : notnull, IEquatable<TKey>
        {
            // Strip any prior ISagaStore<TSaga,TKey> registrations (the
            // InMemory default emitted upstream) and add the EfCore concrete
            // store. EfCoreSagaStore<TSaga, TKey> shares ISagaStore's looser
            // TSaga constraint and runtime-checks ISagaPersistableState in
            // its constructor, so this registration is a vanilla
            // closed-generic ServiceDescriptor — no MakeGenericType.
            var services = builder.Services;
            for (int i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(ISagaStore<TSaga, TKey>))
                {
                    services.RemoveAt(i);
                }
            }
            services.Add(new ServiceDescriptor(
                typeof(ISagaStore<TSaga, TKey>),
                typeof(EfCoreSagaStore<TSaga, TKey>),
                ServiceLifetime.Scoped));
        }
    }
}
