using System.Threading;
using System.Threading.Tasks;
using AotSmoke;
using ZeroAlloc.Mediator;

// Hand-written partial of IMediator. AotSmoke uses option A (RecordingMediator-style
// direct dispatch) to stay self-contained: the published 2.0.x
// ZeroAlloc.Mediator.Generator nupkg shipped its analyzer DLL under lib/ instead
// of analyzers/dotnet/cs/ which makes it a no-op via NuGet (fixed in 2.0.1, but
// not yet on the resolved NuGet feed for this repo's central package pin).
//
// TODO(post-2.0.1): once Mediator.Generator 2.0.1+ is the resolved version on
// this repo's central package pin, delete this partial, add the generator
// PackageReference to AotSmoke.csproj, and switch the smoke test to use the
// generated dispatcher / IMediator.Publish.
namespace ZeroAlloc.Mediator;

public partial interface IMediator
{
    ValueTask<Unit> Send(global::AotSmoke.ReserveStockCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::AotSmoke.ChargeCustomerCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::AotSmoke.ShipOrderCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::AotSmoke.CancelReservationCommand request, CancellationToken ct = default);
    ValueTask<Unit> Send(global::AotSmoke.RefundPaymentCommand request, CancellationToken ct = default);
}
