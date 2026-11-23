using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.Sdk10Smoke;

// IMediator comes from the Mediator generator, WithOrderSaga from the saga generator: the
// build fails unless the compiler loaded both.
var counters = new Counters();
Counters.Current = counters;

var services = new ServiceCollection();
services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
services.AddMediator();
services.TryAddTransient<ReserveStockHandler>();
services.TryAddTransient<ShipOrderHandler>();
services.AddSaga().WithOrderSaga();
var sp = services.BuildServiceProvider();

var mediator = sp.GetRequiredService<IMediator>();
await mediator.Publish(new OrderPlaced(7), default);
await mediator.Publish(new StockReserved(7), default);

if (counters.Reserve != 1 || counters.Ship != 1)
{
    Console.Error.WriteLine($"FAIL: reserve {counters.Reserve}, ship {counters.Ship}");
    return 1;
}

Console.WriteLine("SDK 10 consumer: generators loaded, saga ran both steps.");
return 0;

namespace ZeroAlloc.Saga.Sdk10Smoke
{
    public sealed record OrderPlaced(int OrderId) : INotification;

    public sealed record StockReserved(int OrderId) : INotification;

    public sealed record ReserveStockCommand(int OrderId) : IRequest;

    public sealed record ShipOrderCommand(int OrderId) : IRequest;

    [Saga]
    public partial class OrderSaga
    {
        [CorrelationKey] public int Correlation(OrderPlaced e) => e.OrderId;

        [CorrelationKey] public int Correlation(StockReserved e) => e.OrderId;

        [Step(Order = 1)]
        public ReserveStockCommand ReserveStock(OrderPlaced e) => new(e.OrderId);

        [Step(Order = 2)]
        public ShipOrderCommand ShipOrder(StockReserved e) => new(e.OrderId);
    }

    internal sealed class Counters
    {
        public int Reserve;
        public int Ship;

        public static Counters? Current { get; set; }
    }

    internal sealed class ReserveStockHandler : IRequestHandler<ReserveStockCommand, Unit>
    {
        public ValueTask<Unit> Handle(ReserveStockCommand req, CancellationToken ct)
        {
            Counters.Current!.Reserve++;
            return new(Unit.Value);
        }
    }

    internal sealed class ShipOrderHandler : IRequestHandler<ShipOrderCommand, Unit>
    {
        public ValueTask<Unit> Handle(ShipOrderCommand req, CancellationToken ct)
        {
            Counters.Current!.Ship++;
            return new(Unit.Value);
        }
    }
}
