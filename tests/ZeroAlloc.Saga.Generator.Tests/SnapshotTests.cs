using System.Threading.Tasks;

namespace ZeroAlloc.Saga.Generator.Tests;

public class SnapshotTests
{
    private const string Header = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;
        """;

    [Fact]
    public Task Minimal_SingleStep_NoCompensation()
    {
        var src = Header + """

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;

            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record ReserveStockCommand(OrderId OrderId) : IRequest;

            [Saga]
            public partial class SingleStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task TwoStep_NoCompensation()
    {
        var src = Header + """

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;

            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record StockReserved(OrderId OrderId) : INotification;

            public sealed record ReserveStockCommand(OrderId OrderId) : IRequest;
            public sealed record ChargeCommand(OrderId OrderId) : IRequest;

            [Saga]
            public partial class TwoStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;

                [Step(Order = 1)] public ReserveStockCommand Reserve(OrderPlaced e) => new(e.OrderId);
                [Step(Order = 2)] public ChargeCommand Charge(StockReserved e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task ThreeStep_FullCompensation()
    {
        var src = Header + """

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;

            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record StockReserved(OrderId OrderId) : INotification;
            public sealed record PaymentCharged(OrderId OrderId) : INotification;
            public sealed record PaymentDeclined(OrderId OrderId) : INotification;

            public sealed record ReserveCommand(OrderId OrderId) : IRequest;
            public sealed record ChargeCommand(OrderId OrderId) : IRequest;
            public sealed record ShipCommand(OrderId OrderId) : IRequest;
            public sealed record CancelReservationCommand(OrderId OrderId) : IRequest;
            public sealed record RefundCommand(OrderId OrderId) : IRequest;

            [Saga]
            public partial class ThreeStepSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e)     => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockReserved e)   => e.OrderId;
                [CorrelationKey] public OrderId Correlation(PaymentCharged e)  => e.OrderId;
                [CorrelationKey] public OrderId Correlation(PaymentDeclined e) => e.OrderId;

                [Step(Order = 1, Compensate = nameof(CancelReservation))]
                public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);

                [Step(Order = 2, Compensate = nameof(Refund), CompensateOn = typeof(PaymentDeclined))]
                public ChargeCommand Charge(StockReserved e) => new(e.OrderId);

                [Step(Order = 3)]
                public ShipCommand Ship(PaymentCharged e) => new(e.OrderId);

                public CancelReservationCommand CancelReservation() => new(default);
                public RefundCommand Refund() => new(default);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task WithStateFields()
    {
        var src = Header + """

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;

            public sealed record OrderPlaced(OrderId OrderId, decimal Total) : INotification;
            public sealed record ChargeCommand(OrderId OrderId, decimal Total) : IRequest;

            [Saga]
            public partial class StateFieldSaga
            {
                public OrderId OrderId { get; private set; }
                public decimal Total { get; private set; }

                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;

                [Step(Order = 1)]
                public ChargeCommand Reserve(OrderPlaced e)
                {
                    OrderId = e.OrderId;
                    Total = e.Total;
                    return new(e.OrderId, e.Total);
                }
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task MultipleFailurePaths()
    {
        var src = Header + """

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;

            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record StockReserved(OrderId OrderId) : INotification;
            public sealed record ChargedOk(OrderId OrderId) : INotification;
            public sealed record StockOutOfStock(OrderId OrderId) : INotification;
            public sealed record PaymentFailed(OrderId OrderId) : INotification;

            public sealed record ReserveCommand(OrderId OrderId) : IRequest;
            public sealed record ChargeCommand(OrderId OrderId) : IRequest;
            public sealed record ShipCommand(OrderId OrderId) : IRequest;
            public sealed record CancelReserveCommand(OrderId OrderId) : IRequest;
            public sealed record RefundCommand(OrderId OrderId) : IRequest;

            [Saga]
            public partial class MultiFailureSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockReserved e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(ChargedOk e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(StockOutOfStock e) => e.OrderId;
                [CorrelationKey] public OrderId Correlation(PaymentFailed e) => e.OrderId;

                [Step(Order = 1, Compensate = nameof(CancelReserve), CompensateOn = typeof(StockOutOfStock))]
                public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);

                [Step(Order = 2, Compensate = nameof(Refund), CompensateOn = typeof(PaymentFailed))]
                public ChargeCommand Charge(StockReserved e) => new(e.OrderId);

                [Step(Order = 3)]
                public ShipCommand Ship(ChargedOk e) => new(e.OrderId);

                public CancelReserveCommand CancelReserve() => new(default);
                public RefundCommand Refund() => new(default);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }

    [Fact]
    public Task TwoSagasInSameProject()
    {
        var src = Header + """

            namespace Sample;

            public readonly record struct OrderId(int V) : IEquatable<OrderId>;

            public sealed record OrderPlaced(OrderId OrderId) : INotification;
            public sealed record OrderShipped(OrderId OrderId) : INotification;

            public sealed record ReserveCommand(OrderId OrderId) : IRequest;
            public sealed record AuditCommand(OrderId OrderId) : IRequest;

            [Saga]
            public partial class FulfillmentSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderPlaced e) => e.OrderId;
                [Step(Order = 1)] public ReserveCommand Reserve(OrderPlaced e) => new(e.OrderId);
            }

            [Saga]
            public partial class AuditSaga
            {
                [CorrelationKey] public OrderId Correlation(OrderShipped e) => e.OrderId;
                [Step(Order = 1)] public AuditCommand Audit(OrderShipped e) => new(e.OrderId);
            }
            """;
        return Verifier.Verify(GeneratorTestHost.Run(src)).UseDirectory("Snapshots");
    }
}
