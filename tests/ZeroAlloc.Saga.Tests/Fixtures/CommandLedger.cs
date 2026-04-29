using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga.Tests.Fixtures;

/// <summary>
/// Shared, thread-safe ledger that recording <see cref="IRequestHandler{TRequest,TResponse}"/>
/// implementations append into. Tests inspect the ledger to assert which commands the saga
/// dispatched (and in which order).
/// </summary>
/// <remarks>
/// The ledger is exposed via the <see cref="Current"/> AsyncLocal slot rather than constructor
/// injection because <see cref="ZeroAlloc.Mediator.Mediator"/>'s generated dispatcher requires
/// handler types to expose a parameterless constructor (the no-factory fallback path is
/// <c>factory?.Invoke() ?? new THandler()</c>). Each test sets <see cref="Current"/> at host
/// build time; recording handlers read from it on every invocation.
/// </remarks>
public sealed class CommandLedger
{
    private static readonly AsyncLocal<CommandLedger?> _current = new();

    public static CommandLedger? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    private readonly System.Threading.Lock _gate = new();
    private readonly List<object> _all = new();
    private readonly Func<object, Task>? _onCommand;

    public CommandLedger(Func<object, Task>? onCommand = null) => _onCommand = onCommand;

    public IReadOnlyList<object> AllCommands { get { lock (_gate) return _all.ToArray(); } }

    public IReadOnlyList<T> CommandsOfType<T>()
    {
        lock (_gate)
        {
            var list = new List<T>();
            foreach (var cmd in _all)
                if (cmd is T t) list.Add(t);
            return list;
        }
    }

    public async ValueTask RecordAsync(object cmd)
    {
        lock (_gate) _all.Add(cmd);
        if (_onCommand is not null)
            await _onCommand(cmd).ConfigureAwait(false);
    }
}

// Recording IRequestHandlers — one per command type emitted by the saga fixtures. Each
// handler appends to the ambient CommandLedger (set via AsyncLocal at test setup time).
// With the Mediator source generator running in this test project, these handlers light
// up real IMediator.Send dispatch end-to-end (no hand-rolled IMediator partial required).
public sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
{
    public async ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class ChargeCustomerHandler : IRequestHandler<ChargeCustomerCommand, Unit>
{
    public async ValueTask<Unit> Handle(ChargeCustomerCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
{
    public async ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class CancelReservationHandler : IRequestHandler<CancelReservationCommand, Unit>
{
    public async ValueTask<Unit> Handle(CancelReservationCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class RefundPaymentHandler : IRequestHandler<RefundPaymentCommand, Unit>
{
    public async ValueTask<Unit> Handle(RefundPaymentCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}

public sealed class AuditOrderHandler : IRequestHandler<AuditOrderCommand, Unit>
{
    public async ValueTask<Unit> Handle(AuditOrderCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}
