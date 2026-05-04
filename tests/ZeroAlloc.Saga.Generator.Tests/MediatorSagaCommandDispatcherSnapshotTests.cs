using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Generator.Tests;

public class MediatorSagaCommandDispatcherSnapshotTests
{
    [Fact]
    public Task EmitsDispatcher_WithSwitch_OverAllStepCommands()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record StockReserved(OrderId OrderId) : INotification;

            public readonly record struct ReserveStockCommand(OrderId OrderId) : IRequest<Unit>;
            public readonly record struct ChargeCardCommand(OrderId OrderId) : IRequest<Unit>;

            [Saga]
            public partial class TwoStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
                [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 2)] public ChargeCardCommand Charge(StockReserved e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task EmitsSingleDispatcher_AcrossMultipleSagas()
    {
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;
            public readonly record struct ShipId(int V) : IEquatable<ShipId>;
            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record ShipQueued(ShipId ShipId) : INotification;

            public readonly record struct ReserveCmd(OrderId OrderId) : IRequest<Unit>;
            public readonly record struct LabelCmd(ShipId ShipId) : IRequest<Unit>;

            [Saga]
            public partial class OrderSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCmd Reserve(OrderPlaced e) => new(e.OrderId);
            }

            [Saga]
            public partial class ShipSaga
            {
                [CorrelationKey] public ShipId Correlation(ShipQueued e) => e.ShipId;
                [Step(Order = 1)] public LabelCmd Label(ShipQueued e) => new(e.ShipId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }
}
