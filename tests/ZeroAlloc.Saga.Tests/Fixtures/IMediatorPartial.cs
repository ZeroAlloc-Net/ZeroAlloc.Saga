using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

// Hand-written partial of IMediator surfacing the Send overloads the saga handlers
// emit calls for. Normally these come from ZeroAlloc.Mediator.Generator running in
// the user project; the published v2 generator nupkg is shipped with a broken
// analyzer layout (lib/ instead of analyzers/dotnet/cs/), so the saga test project
// hand-rolls just the surface area it needs without taking a generator dependency.
namespace ZeroAlloc.Mediator;

public partial interface IMediator
{
    ValueTask<Unit> Send(global::ZeroAlloc.Saga.Tests.Fixtures.ReserveStockCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::ZeroAlloc.Saga.Tests.Fixtures.ChargeCustomerCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::ZeroAlloc.Saga.Tests.Fixtures.ShipOrderCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::ZeroAlloc.Saga.Tests.Fixtures.CancelReservationCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::ZeroAlloc.Saga.Tests.Fixtures.RefundPaymentCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::ZeroAlloc.Saga.Tests.Fixtures.AuditOrderCommand request, CancellationToken ct = default);
}
