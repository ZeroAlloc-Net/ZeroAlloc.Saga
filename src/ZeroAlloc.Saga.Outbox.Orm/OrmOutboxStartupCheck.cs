using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;

namespace ZeroAlloc.Saga.Outbox.Orm;

/// <summary>
/// Fails the host start when the <see cref="IOutboxStore"/> ZeroAlloc.Outbox's worker claims from
/// is not the ORM outbox store that <see cref="SagaOutboxOrmBuilderExtensions.WithOrmOutbox"/>
/// writes saga commands through.
/// </summary>
/// <remarks>
/// <see cref="OrmOutboxTransactionContributor"/> writes each saga command with
/// <see cref="OrmOutboxStore.EnqueueInTransactionAsync"/>, into the <c>OutboxMessages</c> table
/// on the saga store's connection. Any other outbox store, such as
/// <c>AddOutbox().WithEfCore&lt;TContext&gt;()</c>, would leave the worker polling somewhere else,
/// so no saga command would ever be dispatched. Like <c>WithOutbox()</c>'s own check, this runs in
/// <see cref="StartingAsync"/>, before the worker starts.
/// </remarks>
internal sealed class OrmOutboxStartupCheck : IHostedLifecycleService
{
    private readonly IServiceProvider _services;

    public OrmOutboxStartupCheck(IServiceProvider services) => _services = services;

    internal static string NotOrmOutboxStoreMessage(IOutboxStore? store) =>
        "ZeroAlloc.Saga.Outbox.Orm.WithOrmOutbox(): saga commands are written to the outbox through " +
        "ZeroAlloc.Outbox.Orm's OrmOutboxStore, inside the saga store's transaction, but " +
        (store is null
            ? "no IOutboxStore is registered. "
            : $"the IOutboxStore ZeroAlloc.Outbox's worker claims from is {FriendlyName(store.GetType())}. ") +
        "Register the ORM outbox store with services.AddOutbox().WithOrm(), passing the dialect of your " +
        "database, and no other outbox store.\n" +
        "See https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md#zeroallocsagaorm-atomic-with-withormoutbox";

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var scope = _services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var scoped = scope.ServiceProvider;

            // Another unit of work replaced WithOrmOutbox()'s, so this pairing is not in play.
            if (scoped.GetService<ISagaUnitOfWork>() is not OrmSagaUnitOfWork)
                return;

            // A missing store is reported by WithOutbox()'s own check, with the supported setups.
            var store = scoped.GetService<IOutboxStore>();
            if (store is null or OrmOutboxStore)
                return;

            throw new InvalidOperationException(NotOrmOutboxStoreMessage(store));
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
