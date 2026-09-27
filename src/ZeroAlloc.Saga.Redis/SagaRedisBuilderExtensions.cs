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
    /// throws <see cref="InvalidOperationException"/> via <see cref="SagaBuilderMutationExtensions.SetDurableStore"/>.</para>
    ///
    /// <para>Composition with <c>WithOutbox()</c>: add <c>ZeroAlloc.Saga.Outbox.Redis</c> and
    /// call <c>WithRedisOutbox()</c> after <c>WithOutbox()</c>. Its <c>RedisSagaUnitOfWork</c>
    /// batches outbox writes into this store's MULTI/EXEC, so saga state and outbox row commit
    /// together. Without it, <c>WithOutbox()</c>'s default <see cref="ISagaUnitOfWork"/> writes
    /// through the configured <c>IOutboxStore</c>, which is not atomic with a Redis saga
    /// store.</para>
    /// </remarks>
    public static ISagaBuilder WithRedisStore(this ISagaBuilder builder, Action<RedisSagaStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.SetDurableStore("WithRedisStore()");

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
