using System.Data.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// Registers ZeroAlloc.ORM as the durable saga store.
/// </summary>
public static class SagaOrmBuilderExtensions
{
    /// <summary>
    /// Persists sagas through ZeroAlloc.ORM, against the
    /// <see cref="IAsyncDbConnection"/> already registered in the container.
    /// </summary>
    /// <param name="builder">The saga builder.</param>
    /// <param name="configure">Optional retry tuning.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Called after a per-saga <c>WithXxxSaga()</c> registration, or a durable
    /// store is already configured.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Register a scoped <see cref="IAsyncDbConnection"/> yourself — this method
    /// deliberately does not, because the connection's lifetime, provider and
    /// connection string belong to the application, not to the saga backend.
    /// </para>
    /// <para>
    /// Create the schema with <see cref="SagaOrmMigrations"/> and the ORM's
    /// <c>MigrationRunner</c>; the store does not create tables on the fly.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddSaga()
    ///         .WithOrmStore(opts => opts.MaxRetryAttempts = 3)
    ///         .WithOrderFulfillmentSaga();
    /// </code>
    /// </example>
    public static ISagaBuilder WithOrmStore(
        this ISagaBuilder builder,
        Action<OrmSagaStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Same ordering guard the other backends apply: a per-saga
        // WithXxxSaga() has already registered ISagaStore<TSaga,TKey>, and the
        // registrar below only rewrites registrations it can see.
        if (HasAnySagaRegistrations(builder.Services))
        {
            throw new InvalidOperationException(
                "WithOrmStore() must be called BEFORE per-saga WithXxxSaga() registrations. " +
                "Reorder your fluent chain so the ORM backend is configured first " +
                "(e.g. services.AddSaga().WithOrmStore().WithOrderFulfillmentSaga()).");
        }

        builder.SetDurableStore("WithOrmStore()");

        var options = new OrmSagaStoreOptions();
        configure?.Invoke(options);
        builder.Services.TryAddSingleton(options);

        // Point the shared SagaRetryOptions at the concrete options instance, so
        // the retry loop and this backend always read the same object.
        for (int i = builder.Services.Count - 1; i >= 0; i--)
        {
            if (builder.Services[i].ServiceType == typeof(SagaRetryOptions))
            {
                builder.Services.RemoveAt(i);
            }
        }
        builder.Services.AddSingleton<SagaRetryOptions>(sp =>
            sp.GetRequiredService<OrmSagaStoreOptions>());

        builder.Services.TryAddScoped(sp =>
            new SagaInstanceRepository(sp.GetRequiredService<IAsyncDbConnection>()));

        SagaStoreRegistrar.SetTypedRegistrar(OrmSagaStoreRegistrar.Instance);
        return builder;
    }

    private static bool HasAnySagaRegistrations(IServiceCollection services)
    {
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

    private sealed class OrmSagaStoreRegistrar : ISagaStoreRegistrar
    {
        public static readonly OrmSagaStoreRegistrar Instance = new();

        public void Register<TSaga, TKey>(ISagaBuilder builder)
            where TSaga : class, new()
            where TKey : notnull, IEquatable<TKey>
        {
            var services = builder.Services;
            for (int i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(ISagaStore<TSaga, TKey>))
                {
                    services.RemoveAt(i);
                }
            }

            // A factory rather than a type descriptor: OrmSagaStore's
            // constructor takes the internal SagaInstanceRepository, which the
            // container could not resolve through a public constructor. The
            // lambda compiles inside this assembly, so it can. This keeps the
            // repository and its row type out of the public surface, where they
            // would otherwise be permanent API for something that is an
            // implementation detail.
            services.Add(new ServiceDescriptor(
                typeof(ISagaStore<TSaga, TKey>),
                sp => new OrmSagaStore<TSaga, TKey>(
                    sp.GetRequiredService<SagaInstanceRepository>(),
                    sp.GetService<ILogger<OrmSagaStore<TSaga, TKey>>>()),
                ServiceLifetime.Scoped));
        }
    }
}
