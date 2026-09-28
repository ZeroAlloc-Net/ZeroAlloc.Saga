using System;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// The <see cref="IOutboxTypeDispatcher"/> for one saga command type.
/// <see cref="SagaOutboxBuilderExtensions.WithOutbox"/> registers one per command type of every
/// registered <see cref="SagaCommandSource"/>, so ZeroAlloc.Outbox's worker dispatches saga
/// commands like any other outbox message, whichever assembly declares them.
/// </summary>
/// <remarks>
/// Registered scoped. The worker resolves it from the same per-batch scope as the
/// <see cref="IOutboxStore"/>, and <paramref name="services"/> is that scope's provider, so the
/// command is deserialised and sent in the store's scope. On the EF path, that is the store's
/// <c>DbContext</c>.
/// </remarks>
internal sealed class SagaCommandOutboxDispatcher : IOutboxTypeDispatcher
{
    private readonly SagaCommandRegistryDispatcher _dispatch;
    private readonly IServiceProvider _services;

    public SagaCommandOutboxDispatcher(string typeName, SagaCommandRegistryDispatcher dispatch, IServiceProvider services)
    {
        TypeName = typeName;
        _dispatch = dispatch;
        _services = services;
    }

    /// <inheritdoc />
    public string TypeName { get; }

    /// <inheritdoc />
    public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => _dispatch(TypeName, payload, _services, ct);
}
