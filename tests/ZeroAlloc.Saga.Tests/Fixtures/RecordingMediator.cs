using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga.Tests.Fixtures;

/// <summary>
/// Test double for <see cref="IMediator"/> that records every command sent to
/// <c>Send</c>. Order is preserved across all command types via a single list.
/// </summary>
public sealed class RecordingMediator : IMediator
{
    private readonly System.Threading.Lock _gate = new();
    private readonly List<object> _all = new();
    private readonly Func<object, Task>? _onSend;

    public RecordingMediator(Func<object, Task>? onSend = null) => _onSend = onSend;

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

    private async ValueTask<Unit> RecordAsync(object cmd)
    {
        lock (_gate) _all.Add(cmd);
        if (_onSend is not null)
            await _onSend(cmd).ConfigureAwait(false);
        return Unit.Value;
    }

    public ValueTask<Unit> Send(ReserveStockCommand request, CancellationToken ct = default) => RecordAsync(request);
    public ValueTask<Unit> Send(ChargeCustomerCommand request, CancellationToken ct = default) => RecordAsync(request);
    public ValueTask<Unit> Send(ShipOrderCommand request, CancellationToken ct = default) => RecordAsync(request);
    public ValueTask<Unit> Send(CancelReservationCommand request, CancellationToken ct = default) => RecordAsync(request);
    public ValueTask<Unit> Send(RefundPaymentCommand request, CancellationToken ct = default) => RecordAsync(request);
    public ValueTask<Unit> Send(AuditOrderCommand request, CancellationToken ct = default) => RecordAsync(request);
}
