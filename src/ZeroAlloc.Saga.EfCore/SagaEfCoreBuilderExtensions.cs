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
    /// <para>
    /// MUST be called BEFORE per-saga <c>AddXxxSaga()</c> registrations.
    /// Order-sensitive because per-saga registrations capture
    /// <see cref="SagaRetryOptions"/> at registration time; calling
    /// <see cref="WithEfCoreStore{TContext}"/> after a saga is added would
    /// rebind the options after handlers were already wired to the old
    /// instance, producing silently-wrong retry behaviour. Violations are
    /// detected and throw <see cref="InvalidOperationException"/> with a
    /// reorder hint.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a per-saga <c>AddXxxSaga()</c> registration has already
    /// happened on this builder. Reorder the fluent chain so
    /// <see cref="WithEfCoreStore{TContext}"/> is called first.
    /// </exception>
    public static ISagaBuilder WithEfCoreStore<TContext>(
        this ISagaBuilder builder,
        Action<EfCoreSagaStoreOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Order violation guard: a per-saga AddXxxSaga() registers
        // ISagaCompensationDispatcher<TSaga>. If we see ANY descriptor whose
        // ServiceType is closed-generic ISagaCompensationDispatcher<>, we
        // know a per-saga registration has already happened.
        if (HasAnySagaRegistrations(builder.Services))
        {
            throw new InvalidOperationException(
                "WithEfCoreStore<TContext>() must be called BEFORE per-saga AddXxxSaga() registrations. " +
                "Reorder your fluent chain so the EF Core backend is configured first " +
                "(e.g. services.AddSaga().WithEfCoreStore<TContext>().AddOrderFulfillmentSaga()).");
        }

        builder.SetEfCoreBackend();

        var options = new EfCoreSagaStoreOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        // Replace the default SagaRetryOptions registered by AddSaga() with
        // a factory pointing at the concrete EfCoreSagaStoreOptions so both
        // registrations always resolve to the same instance. Going through
        // a factory (rather than registering the same object under two
        // service types) means any future lifecycle change on
        // EfCoreSagaStoreOptions carries through to the SagaRetryOptions
        // alias automatically.
        for (int i = builder.Services.Count - 1; i >= 0; i--)
        {
            if (builder.Services[i].ServiceType == typeof(SagaRetryOptions))
            {
                builder.Services.RemoveAt(i);
            }
        }
        builder.Services.AddSingleton<SagaRetryOptions>(sp =>
            sp.GetRequiredService<EfCoreSagaStoreOptions>());

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

    private static bool HasAnySagaRegistrations(IServiceCollection services)
    {
        // ISagaCompensationDispatcher<TSaga> is registered per-saga by the
        // generator-emitted AddXxxSaga() extension. A closed-generic
        // descriptor of that interface is an unambiguous "a saga has been
        // added" marker, regardless of which saga or which key type.
        for (int i = 0; i < services.Count; i++)
        {
            var st = services[i].ServiceType;
            if (st.IsGenericType && !st.IsGenericTypeDefinition
                && st.GetGenericTypeDefinition() == typeof(ISagaCompensationDispatcher<>))
            {
                return true;
            }
        }
        return false;
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
