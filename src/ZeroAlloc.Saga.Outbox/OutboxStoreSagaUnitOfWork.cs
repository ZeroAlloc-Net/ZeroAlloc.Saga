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
///   <c>SaveChangesAsync</c> commits both atomically.</description></item>
///   <item><description>InMemory <see cref="IOutboxStore"/> implementations that
///   auto-commit via the default-interface-method fallback do NOT guarantee
///   atomicity — the outbox row is persisted before the saga state save.
///   See <c>docs/outbox.md</c>.</description></item>
/// </list>
/// Backend-specific bridge packages (<c>ZeroAlloc.Saga.Redis</c>) override this
/// with their own <see cref="ISagaUnitOfWork"/> registration that participates
/// in the backend's transactional primitive (e.g. Redis MULTI/EXEC).
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
