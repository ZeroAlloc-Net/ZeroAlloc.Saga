using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Serialisation;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// <see cref="ISagaCommandDispatcher"/> that serializes the saga step command via
/// <see cref="ISerializer{T}"/> resolved from DI and writes it to <see cref="IOutboxStore"/>.
/// The outbox row commits atomically with the saga state save when both stores share a
/// transactional substrate (e.g. EfCore's scoped DbContext).
/// </summary>
public sealed class OutboxSagaCommandDispatcher : ISagaCommandDispatcher
{
    private readonly IOutboxStore _store;
    private readonly IServiceProvider _services;

    public OutboxSagaCommandDispatcher(IOutboxStore store, IServiceProvider services)
    {
        _store = store;
        _services = services;
    }

    /// <inheritdoc />
    public async ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct)
        where TCommand : IRequest<Unit>
    {
        var serializer = _services.GetRequiredService<ISerializer<TCommand>>();
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(buffer, cmd);
        await _store.EnqueueAsync(
            typeName: typeof(TCommand).FullName!,
            payload: buffer.WrittenMemory,
            transaction: null,
            ct: ct).ConfigureAwait(false);
    }
}
