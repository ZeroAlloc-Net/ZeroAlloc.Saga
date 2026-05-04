using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Delegate that dispatches a single outbox-fetched saga command. The default implementation
/// (registered by <see cref="SagaOutboxBuilderExtensions.WithOutbox"/>) reflects to the
/// generator-emitted <c>ZeroAlloc.Saga.Generated.SagaCommandRegistry.DispatchAsync</c> in the
/// consumer's compilation. Tests may register their own delegate to short-circuit dispatch.
/// </summary>
/// <remarks>
/// The delegate intentionally does NOT take <c>IMediator</c> in its signature: the
/// <c>IMediator</c> type is generator-emitted per consumer compilation, so this library
/// cannot reference it directly. The default reflective implementation resolves the
/// concrete <c>IMediator</c> from <paramref name="services"/> before invoking the registry.
/// </remarks>
public delegate ValueTask SagaCommandRegistryDispatcher(
    string typeName,
    ReadOnlyMemory<byte> bytes,
    IServiceProvider services,
    CancellationToken ct);
