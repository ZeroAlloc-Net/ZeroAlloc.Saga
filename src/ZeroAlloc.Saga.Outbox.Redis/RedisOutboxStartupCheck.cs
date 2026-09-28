using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// Fails the host start when ZeroAlloc.Outbox's worker would claim from another outbox store than
/// the Redis outbox that <see cref="SagaOutboxRedisBuilderExtensions.WithRedisOutbox"/> writes
/// saga commands to.
/// </summary>
/// <remarks>
/// <see cref="RedisSagaUnitOfWork"/> writes each saga command into the Redis outbox inside the
/// saga store's <c>MULTI/EXEC</c>. <c>WithRedisOutbox()</c> registers <see cref="RedisOutboxStore"/>
/// as the <see cref="IOutboxStore"/> the worker claims from, but an <see cref="IOutboxStore"/>
/// registered after it, such as <c>AddOutbox().WithEfCore&lt;TContext&gt;()</c>, wins. The worker
/// would then never claim a saga command. Like <c>WithOutbox()</c>'s own check, this runs in
/// <see cref="StartingAsync"/>, before the worker starts.
/// </remarks>
internal sealed class RedisOutboxStartupCheck : IHostedLifecycleService
{
    private readonly IServiceProvider _services;

    public RedisOutboxStartupCheck(IServiceProvider services) => _services = services;

    internal static string OtherOutboxStoreMessage(string outboxStore) =>
        "ZeroAlloc.Saga.Outbox.Redis.WithRedisOutbox(): the saga store WithRedisStore() writes each saga " +
        "command to the Redis outbox, the store RedisOutboxStore, but ZeroAlloc.Outbox's worker claims from " +
        $"the outbox store {outboxStore}, registered after WithRedisOutbox(). The worker would never dispatch " +
        "a saga command. Remove that registration: WithRedisOutbox() supplies the outbox store, so call " +
        "services.AddOutbox() without WithEfCore<TContext>() or WithOrm().\n" +
        "See https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md#supported-pairings";

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var scope = _services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var scoped = scope.ServiceProvider;
            // Another unit of work replaced WithRedisOutbox()'s, so this pairing is not in play.
            if (scoped.GetService<ISagaUnitOfWork>() is not RedisSagaUnitOfWork)
                return;

            // A missing store is reported by WithOutbox()'s own check.
            var store = scoped.GetService<IOutboxStore>();
            if (store is null or RedisOutboxStore)
                return;

            throw new InvalidOperationException(OtherOutboxStoreMessage(FriendlyName(store.GetType())));
        }
    }

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        var arguments = string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), FriendlyName));
        return $"{(tick < 0 ? type.Name : type.Name[..tick])}<{arguments}>";
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
