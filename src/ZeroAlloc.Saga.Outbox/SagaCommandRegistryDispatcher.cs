using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Delegate that dispatches a single saga command read from the outbox. Every saga command's
/// <c>IOutboxTypeDispatcher</c>, registered by <see cref="SagaOutboxBuilderExtensions.WithOutbox"/>,
/// calls it with its own type name and its own scoped service provider. The default
/// implementation calls <see cref="SagaCommandSource.DispatchSerializedAsync"/> on the source of the
/// assembly whose sagas return that command type, which forwards to that assembly's
/// generator-emitted <c>ZeroAlloc.Saga.Generated.SagaCommandRegistry</c>. Register your own before
/// <c>WithOutbox()</c> to replace it, for example in tests.
/// </summary>
/// <remarks>
/// The delegate intentionally does NOT take <c>IMediator</c> in its signature: the
/// <c>IMediator</c> type is generator-emitted per consumer compilation, so this library
/// cannot reference it directly. The generated source, which lives in that compilation, resolves
/// its <c>IMediator</c> from <paramref name="services"/> before invoking the registry.
/// </remarks>
public delegate ValueTask SagaCommandRegistryDispatcher(
    string typeName,
    ReadOnlyMemory<byte> bytes,
    IServiceProvider services,
    CancellationToken ct);
