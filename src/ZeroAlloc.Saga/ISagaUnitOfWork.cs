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
/// call from the same DI scope. If the saga state save fails (OCC conflict,
/// etc.), the enlisted outbox write MUST also be discarded.</para>
///
/// <para>Backends own the meaning of "atomic": <c>ZeroAlloc.Saga.EfCore</c> uses
/// a shared scoped <c>DbContext</c> whose <c>SaveChangesAsync</c> commits both
/// the saga update and any tracked outbox entity. <c>ZeroAlloc.Saga.Redis</c>
/// (Stage 2) uses MULTI/EXEC across the saga key and the outbox stream within
/// the same scope.</para>
///
/// <para>The default implementation in <c>ZeroAlloc.Saga.Outbox</c>'s
/// <c>WithOutbox()</c> wraps <see cref="ZeroAlloc.Outbox.IOutboxStore"/>'s
/// <c>EnqueueDeferredAsync</c> directly — sufficient when the
/// <c>IOutboxStore</c> implementation already honors deferred-write semantics
/// (<c>EfCoreOutboxStore</c> does; the InMemory backend's auto-commit fallback
/// does not, but is documented as not-atomic for that combination).</para>
/// </remarks>
public interface ISagaUnitOfWork
{
    /// <summary>
    /// Stage an outbox row write to be committed atomically with the next
    /// <see cref="ISagaStore{TSaga,TKey}.SaveAsync"/> call from this scope.
    /// </summary>
    /// <param name="typeName">Fully-qualified name of the command type, used by
    /// the poller's reflective dispatch path to identify the deserializer.</param>
    /// <param name="payload">Serialized command bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask EnlistOutboxRowAsync(string typeName, ReadOnlyMemory<byte> payload, CancellationToken ct);
}
