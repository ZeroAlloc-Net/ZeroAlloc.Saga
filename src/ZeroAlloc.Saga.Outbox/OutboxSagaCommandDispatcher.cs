using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// <see cref="ISagaCommandDispatcher"/> that serializes the saga step command via
/// <see cref="ISerializer{T}"/> resolved from DI and enlists the bytes with the
/// scope's <see cref="ISagaUnitOfWork"/>. The unit of work is responsible for
/// committing the enlisted writes atomically with the next saga state save.
/// </summary>
/// <remarks>
/// <para>The dispatcher is backend-agnostic: with <c>WithEfCoreStore()</c>, the
/// default <see cref="OutboxStoreSagaUnitOfWork"/> delegates to
/// <c>EfCoreOutboxStore.EnqueueDeferredAsync</c> (which Adds a tracked entity to
/// the shared scoped <c>DbContext</c>); with <c>WithRedisStore()</c> (Stage 2),
/// the Redis-specific unit of work buffers the writes for the saga store's
/// MULTI/EXEC.</para>
///
/// <para>Atomicity contract: an OCC retry that fails the saga state save also
/// discards the enlisted outbox writes. See <c>docs/outbox.md</c>.</para>
/// </remarks>
public sealed class OutboxSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly ISagaUnitOfWork _uow;
    private readonly IServiceProvider _services;

    public OutboxSagaCommandDispatcher(ISagaUnitOfWork uow, IServiceProvider services)
    {
        _uow = uow;
        _services = services;
    }

    /// <inheritdoc />
    public async ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        var serializer = _services.GetRequiredService<ISerializer<TCommand>>();
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(buffer, cmd);
        await _uow.EnlistOutboxRowAsync(
            typeName: typeof(TCommand).FullName!,
            payload: buffer.WrittenMemory,
            ct: ct).ConfigureAwait(false);
    }
}
