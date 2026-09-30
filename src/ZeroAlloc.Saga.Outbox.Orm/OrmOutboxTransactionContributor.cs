using System.Data.Async;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Saga.Orm;

namespace ZeroAlloc.Saga.Outbox.Orm;

/// <summary>
/// Writes the rows <see cref="OrmSagaUnitOfWork"/> buffered into the ORM saga store's transaction,
/// through <see cref="OrmOutboxStore.EnqueueInTransactionAsync"/>, so they commit with the saga
/// row.
/// </summary>
/// <remarks>
/// The store is resolved only when there is a row to write, and must be the
/// <see cref="IOutboxStore"/> ZeroAlloc.Outbox's worker claims from. Writing through another
/// store's instance would put rows where the worker never looks. The startup check registered by
/// <see cref="SagaOutboxOrmBuilderExtensions.WithOrmOutbox"/> reports a mismatch before the host
/// starts; this check covers a container that is never started as a host.
/// </remarks>
internal sealed class OrmOutboxTransactionContributor : IOrmSagaTransactionContributor
{
    private readonly OrmSagaUnitOfWork _uow;
    private readonly IServiceProvider _services;

    public OrmOutboxTransactionContributor(OrmSagaUnitOfWork uow, IServiceProvider services)
    {
        _uow = uow;
        _services = services;
    }

    /// <inheritdoc />
    public async ValueTask ContributeAsync(IAsyncDbTransaction transaction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var pending = _uow.Drain();
        if (pending.Length == 0)
            return;

        var outboxStore = _services.GetService<IOutboxStore>();
        if (outboxStore is not OrmOutboxStore store)
            throw new InvalidOperationException(OrmOutboxStartupCheck.NotOrmOutboxStoreMessage(outboxStore));

        foreach (var write in pending)
        {
            await store.EnqueueInTransactionAsync(write.TypeName, write.Payload, transaction, ct)
                .ConfigureAwait(false);
        }
    }
}
