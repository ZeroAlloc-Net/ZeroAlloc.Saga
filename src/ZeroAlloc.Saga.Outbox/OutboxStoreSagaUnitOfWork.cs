using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Default <see cref="ISagaUnitOfWork"/> implementation that delegates outbox-row
/// enlistment directly to <see cref="IOutboxStore.EnqueueDeferredAsync"/>.
/// </summary>
/// <remarks>
/// Atomicity guarantee depends on the underlying <see cref="IOutboxStore"/>:
/// <list type="bullet">
///   <item><description><c>EfCoreOutboxStore</c> (when paired with
///   <c>WithEfCoreStore&lt;TContext&gt;()</c>) Adds a tracked entity to the
///   shared scoped <c>DbContext</c>; <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/>'s
///   <c>SaveChangesAsync</c> commits both atomically. With any other saga store nothing saves
///   that <c>DbContext</c>, so the startup check <c>WithOutbox()</c> registers fails the host
///   start rather than let every command be lost.</description></item>
///   <item><description><see cref="IOutboxStore"/> implementations that
///   auto-commit via the default-interface-method fallback, such as the InMemory store and
///   <c>OrmOutboxStore</c>, do NOT guarantee atomicity — the outbox row is persisted before the
///   saga state save. See <c>docs/outbox.md</c>.</description></item>
/// </list>
/// Backend-specific bridge packages override this with their own
/// <see cref="ISagaUnitOfWork"/> registration that participates in the backend's
/// transactional primitive: <c>WithRedisOutbox()</c> in <c>ZeroAlloc.Saga.Outbox.Redis</c> uses
/// Redis MULTI/EXEC, and <c>WithOrmOutbox()</c> in <c>ZeroAlloc.Saga.Outbox.Orm</c> the ORM saga
/// store's database transaction.
/// </remarks>
public sealed class OutboxStoreSagaUnitOfWork : ISagaUnitOfWork
{
    private readonly IOutboxStore _store;

    public OutboxStoreSagaUnitOfWork(IOutboxStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public ValueTask EnlistOutboxRowAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => _store.EnqueueDeferredAsync(typeName, payload, ct);
}
