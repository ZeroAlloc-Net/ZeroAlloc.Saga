using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace ZeroAlloc.Saga.Redis;

/// <summary>
/// Builder-side wiring for the Redis backend. <see cref="WithRedisStore"/> registers the
/// Redis-backed <see cref="ISagaStore{TSaga,TKey}"/> implementation, the
/// <see cref="RedisSagaStoreOptions"/>, and an <see cref="IDatabase"/> resolution from
/// the user-supplied <see cref="IConnectionMultiplexer"/>.
/// </summary>
public static class SagaRedisBuilderExtensions
{
    /// <summary>
    /// Configures the Redis backend on the supplied <see cref="ISagaBuilder"/>. The user
    /// MUST register an <see cref="IConnectionMultiplexer"/> (typically via
    /// <c>services.AddSingleton&lt;IConnectionMultiplexer&gt;(_ => ConnectionMultiplexer.Connect(...))</c>)
    /// BEFORE calling this — the extension wires <see cref="IDatabase"/> resolution from
    /// the registered multiplexer.
    /// </summary>
    /// <param name="builder">The saga builder.</param>
    /// <param name="configure">Optional configurator for <see cref="RedisSagaStoreOptions"/>.</param>
    /// <remarks>
    /// <para>Mutually exclusive with <c>WithEfCoreStore&lt;TContext&gt;()</c>: calling both
    /// throws <see cref="InvalidOperationException"/> via <see cref="SagaBuilderMutationExtensions.SetRedisBackend"/>.</para>
    ///
    /// <para>Composition with <c>WithOutbox()</c>: <em>limited</em> in this release.
    /// <c>WithOutbox()</c> registers <c>OutboxStoreSagaUnitOfWork</c> as the default
    /// <see cref="ISagaUnitOfWork"/>, which delegates to the configured
    /// <c>IOutboxStore.EnqueueDeferredAsync</c>. With an EfCore-backed
    /// <c>IOutboxStore</c> that's atomic; with a non-deferred Redis-backed outbox
    /// store, atomicity is not guaranteed in this release. The forthcoming
    /// <c>ZeroAlloc.Saga.Outbox.Redis</c> package (Stage 3) ships a
    /// <c>RedisSagaUnitOfWork</c> that batches outbox writes into the saga store's
    /// MULTI/EXEC for true atomic dispatch under Redis.</para>
    /// </remarks>
    public static ISagaBuilder WithRedisStore(this ISagaBuilder builder, Action<RedisSagaStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.SetRedisBackend();

        var options = new RedisSagaStoreOptions();
        configure?.Invoke(options);

        var services = builder.Services;
        services.TryAddSingleton(options);
        // Mirror SagaRetryOptions registration so the generator-emitted handler reads
        // the Redis-tuned retry knobs.
        services.Replace(ServiceDescriptor.Singleton<SagaRetryOptions>(options));

        // Resolve IDatabase from the user-registered IConnectionMultiplexer.
        services.TryAddScoped(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

        // Install the typed registrar that the generator-emitted With{Saga}Saga() picks
        // up via SagaStoreRegistrar.Apply<TSaga,TKey>(builder). The registrar replaces
        // the InMemory default with RedisSagaStore<TSaga,TKey> for each saga registered
        // after this call.
        SagaStoreRegistrar.SetTypedRegistrar(new RedisSagaStoreRegistrar());
        return builder;
    }

    private sealed class RedisSagaStoreRegistrar : ISagaStoreRegistrar
    {
        public void Register<TSaga, TKey>(ISagaBuilder builder)
            where TSaga : class, new()
            where TKey : notnull, System.IEquatable<TKey>
        {
            // Replace the InMemory default with the Redis store. Scoped lifetime
            // matches the EfCore backend's pattern — fresh store per per-attempt scope.
            builder.Services.Replace(
                ServiceDescriptor.Scoped<ISagaStore<TSaga, TKey>, RedisSagaStore<TSaga, TKey>>());
        }
    }
}
