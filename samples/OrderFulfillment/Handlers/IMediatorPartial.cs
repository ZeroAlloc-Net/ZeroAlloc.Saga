using System.Threading;
using System.Threading.Tasks;
using OrderFulfillment.Saga;
using ZeroAlloc.Mediator;

// Hand-written partial of IMediator surfacing the typed Send overloads the
// saga generator emits calls to. Normally these come from
// ZeroAlloc.Mediator.Generator running in the user's project; the
// 2.0.x generator nupkg shipped its analyzer DLL under lib/ instead of
// analyzers/dotnet/cs/ which makes it a no-op when consumed from NuGet
// (fixed in 2.0.1, but until that flows through to a stable feed the
// sample hand-rolls just the surface area it needs).
//
// TODO(post-2.0.1): once ZeroAlloc.Mediator.Generator 2.0.1+ is the
// resolved version on this repo's central package pin, delete this
// partial and add the generator package reference to this csproj instead.
namespace ZeroAlloc.Mediator;

public partial interface IMediator
{
    ValueTask<Unit> Send(global::OrderFulfillment.Saga.ReserveStockCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::OrderFulfillment.Saga.ChargeCustomerCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::OrderFulfillment.Saga.ShipOrderCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::OrderFulfillment.Saga.CancelReservationCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::OrderFulfillment.Saga.RefundPaymentCommand request, CancellationToken ct = default);
}
