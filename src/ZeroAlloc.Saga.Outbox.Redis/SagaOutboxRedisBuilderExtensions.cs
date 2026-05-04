using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga.Redis;

namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// Wires up the Redis-native outbox bridge: <see cref="RedisOutboxStore"/> as the
/// <see cref="IOutboxStore"/> the poller reads from, <see cref="RedisSagaUnitOfWork"/>
/// as the <see cref="ISagaUnitOfWork"/> the dispatcher enlists into, and
/// <see cref="RedisOutboxTransactionContributor"/> as the
/// <see cref="IRedisSagaTransactionContributor"/> that drains the unit of work into
/// the saga store's MULTI/EXEC.
/// </summary>
public static class SagaOutboxRedisBuilderExtensions
{
    /// <summary>
    /// Configures the Redis-native outbox bridge on the supplied <see cref="ISagaBuilder"/>.
    /// Call AFTER both <c>WithRedisStore()</c> and <c>WithOutbox()</c> — this extension
    /// replaces the default <see cref="ISagaUnitOfWork"/> registered by <c>WithOutbox()</c>
    /// with a Redis-aware buffer, so a saga step's outbox-row write commits atomically
    /// with the next saga state save inside a single Redis MULTI/EXEC.
    /// </summary>
    /// <param name="builder">The saga builder.</param>
    /// <param name="configure">Optional configurator for <see cref="RedisOutboxOptions"/>.</param>
    public static ISagaBuilder WithRedisOutbox(this ISagaBuilder builder, Action<RedisOutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.IsRedisBackend)
        {
            throw new InvalidOperationException(
                "WithRedisOutbox() requires WithRedisStore() to be configured first. " +
                "The Redis outbox bridge participates in the Redis saga store's MULTI/EXEC; " +
                "without WithRedisStore() there is no Redis transactional context to enlist into.");
        }

        var options = new RedisOutboxOptions();
        configure?.Invoke(options);
        ValidateOptions(options, builder.Services);

        var services = builder.Services;
        services.TryAddSingleton(options);

        // Ensure an IDatabase is resolvable from the user-registered IConnectionMultiplexer.
        // WithRedisStore already registers this, but TryAddScoped is idempotent.
        services.TryAddScoped(sp => sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

        // Replace the default OutboxStoreSagaUnitOfWork (registered by WithOutbox) with the
        // Redis-aware buffer. CRITICAL: the dispatcher resolves ISagaUnitOfWork and the
        // contributor resolves RedisSagaUnitOfWork — both must resolve to the SAME scoped
        // instance, otherwise enlisted writes go to a buffer the contributor never sees.
        // Register the concrete type as scoped, then alias ISagaUnitOfWork via factory.
        services.AddScoped<RedisSagaUnitOfWork>();
        services.Replace(ServiceDescriptor.Scoped<ISagaUnitOfWork>(sp => sp.GetRequiredService<RedisSagaUnitOfWork>()));
        services.AddScoped<IRedisSagaTransactionContributor>(sp =>
            new RedisOutboxTransactionContributor(
                sp.GetRequiredService<RedisSagaUnitOfWork>(),
                sp.GetRequiredService<RedisOutboxOptions>()));

        // Replace any previously-registered IOutboxStore with the Redis-native one so the
        // poller reads from the same Redis key-space the saga store writes to.
        services.Replace(ServiceDescriptor.Scoped<IOutboxStore>(sp =>
            new RedisOutboxStore(
                sp.GetRequiredService<IDatabase>(),
                sp.GetRequiredService<RedisOutboxOptions>())));

        return builder;
    }

    private static void ValidateOptions(RedisOutboxOptions options, IServiceCollection services)
    {
        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
        {
            throw new InvalidOperationException(
                "RedisOutboxOptions.KeyPrefix must be a non-empty, non-whitespace string. " +
                "An empty prefix would put outbox keys in the root key-space and risk colliding " +
                "with other Redis-using subsystems.");
        }

        // Reject prefix collisions with the saga store. If both used the same prefix, an
        // outbox entry id could collide with a saga key, and (more importantly) a wildcard
        // KEYS/SCAN cleanup would treat the two namespaces as one and surprise operators.
        var sagaOptions = TryResolveSagaOptions(services);
        if (sagaOptions is not null && string.Equals(sagaOptions.KeyPrefix, options.KeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"RedisOutboxOptions.KeyPrefix ('{options.KeyPrefix}') must differ from RedisSagaStoreOptions.KeyPrefix. " +
                $"Use distinct prefixes (e.g. '{sagaOptions.KeyPrefix}-outbox') so saga state and outbox entries " +
                "occupy disjoint key-spaces and operational tooling can scope cleanups correctly.");
        }
    }

    /// <summary>
    /// Looks up a previously-registered <see cref="RedisSagaStoreOptions"/> singleton in the
    /// service collection without building the provider. Used to detect <c>KeyPrefix</c>
    /// collisions at registration time (before any scope is created).
    /// </summary>
    private static RedisSagaStoreOptions? TryResolveSagaOptions(IServiceCollection services)
    {
        for (int i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (d.ServiceType == typeof(RedisSagaStoreOptions) && d.ImplementationInstance is RedisSagaStoreOptions opts)
            {
                return opts;
            }
        }
        return null;
    }
}
