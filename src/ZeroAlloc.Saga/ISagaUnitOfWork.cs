using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Saga;

/// <summary>
/// Backend-agnostic abstraction for the transactional context shared between
/// <see cref="ISagaStore{TSaga,TKey}"/>'s state save and any side-effect writes
/// the dispatcher needs to commit atomically with it (most notably the outbox
/// row enqueued by <c>ZeroAlloc.Saga.Outbox</c>).
/// </summary>
/// <remarks>
/// <para>The contract: <see cref="EnlistOutboxRowAsync"/> stages a write that
/// MUST be committed atomically with the next <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/>
/// or <see cref="ISagaStore{TSaga,TKey}.RemoveAsync"/> call from the same DI scope,
/// whichever ends the handler attempt. <c>RemoveAsync</c> commits the enlisted writes even
/// when no saga instance exists, as for a saga that one event both starts and completes.
/// If that save or removal fails (OCC conflict, etc.), the enlisted outbox write MUST also
/// be discarded.</para>
///
/// <para>Backends own the meaning of "atomic": <c>ZeroAlloc.Saga.EfCore</c> uses
/// a shared scoped <c>DbContext</c> whose <c>SaveChangesAsync</c> commits both
/// the saga update and any tracked outbox entity. <c>ZeroAlloc.Saga.Redis</c>,
/// with <c>ZeroAlloc.Saga.Outbox.Redis</c>, uses MULTI/EXEC across the saga key and
/// the outbox entries within the same scope. <c>ZeroAlloc.Saga.Orm</c>, with
/// <c>ZeroAlloc.Saga.Outbox.Orm</c>, writes the outbox rows in the saga store's
/// database transaction.</para>
///
/// <para>The default implementation in <c>ZeroAlloc.Saga.Outbox</c>'s
/// <c>WithOutbox()</c> wraps <see cref="ZeroAlloc.Outbox.IOutboxStore"/>'s
/// <c>EnqueueDeferredAsync</c> directly — sufficient when the
/// <c>IOutboxStore</c> implementation already honors deferred-write semantics
/// (<c>EfCoreOutboxStore</c> does; the InMemory backend's and <c>OrmOutboxStore</c>'s
/// auto-commit fallback does not, and is documented as not-atomic for those
/// combinations, unless a backend unit of work such as <c>WithOrmOutbox()</c>'s
/// replaces the default).</para>
/// </remarks>
public interface ISagaUnitOfWork
{
    /// <summary>
    /// Stage an outbox row write to be committed atomically with the next
    /// <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/> or
    /// <see cref="ISagaStore{TSaga,TKey}.RemoveAsync"/> call from this scope.
    /// </summary>
    /// <param name="typeName">Fully-qualified name of the command type. The outbox dispatcher
    /// registered for that name deserializes and dispatches the entry.</param>
    /// <param name="payload">Serialized command bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask EnlistOutboxRowAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct);
}
