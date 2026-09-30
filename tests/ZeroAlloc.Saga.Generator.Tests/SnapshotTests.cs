using System.Threading.Tasks;

using ZeroAlloc.TestHelpers;

namespace ZeroAlloc.Saga.Generator.Tests;

public class SnapshotTests
{
    private const string Header = """
        using System;
        using ZeroAlloc.Mediator;
        using ZeroAlloc.Saga;
        """;

    [Fact]
    public void Minimal_SingleStep_NoCompensation()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void TwoStep_NoCompensation()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void ThreeStep_FullCompensation()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void WithStateFields()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void MultipleFailurePaths()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void Saga_With_PrimitiveFields_EmitsCorrectWriteCalls()
    {
        var src = Header + """

            namespace Sample;

            public sealed record Started(int Id) : INotification;
            public sealed record DoIt(int Id) : IRequest;

            [Saga]
            public partial class PrimitiveFieldsSaga
            {
                public int Count { get; set; }
                public string Name { get; set; } = "";
                public bool IsActive { get; set; }
                public double Amount { get; set; }

                [CorrelationKey] public int Correlation(Started e) => e.Id;
                [Step(Order = 1)] public DoIt Step1(Started e) => new(e.Id);
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void Saga_With_TypedIdField_EmitsUnderlyingPrimitiveWriteCalls()
    {
        var src = Header + """

            namespace Sample;

            public readonly record struct CustomerId(System.Guid Value) : IEquatable<CustomerId>;

            public sealed record Activated(CustomerId Id) : INotification;
            public sealed record GreetCommand(CustomerId Id) : IRequest;

            [Saga]
            public partial class TypedIdFieldSaga
            {
                public CustomerId CustomerId { get; set; }

                [CorrelationKey] public CustomerId Correlation(Activated e) => e.Id;
                [Step(Order = 1)] public GreetCommand Greet(Activated e) => new(e.Id);
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void Saga_With_NullableField_EmitsFlagBytePrefix()
    {
        var src = Header + """

            namespace Sample;

            public sealed record Started(int Id) : INotification;
            public sealed record DoIt(int Id) : IRequest;

            [Saga]
            public partial class NullableFieldSaga
            {
                public int? OptionalCount { get; set; }
                public System.DateTime? LastSeen { get; set; }

                [CorrelationKey] public int Correlation(Started e) => e.Id;
                [Step(Order = 1)] public DoIt Step1(Started e) => new(e.Id);
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void Saga_With_EnumField_EmitsUnderlyingType()
    {
        var src = Header + """

            namespace Sample;

            public enum OrderStatus : byte { Pending, Shipped, Cancelled }

            public sealed record Started(int Id) : INotification;
            public sealed record DoIt(int Id) : IRequest;

            [Saga]
            public partial class EnumFieldSaga
            {
                public OrderStatus Status { get; set; }

                [CorrelationKey] public int Correlation(Started e) => e.Id;
                [Step(Order = 1)] public DoIt Step1(Started e) => new(e.Id);
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void Saga_With_NotSagaState_FieldExcluded()
    {
        var src = Header + """

            namespace Sample;
            using System.Collections.Generic;

            public sealed record Started(int Id) : INotification;
            public sealed record DoIt(int Id) : IRequest;

            [Saga]
            public partial class ExcludedFieldSaga
            {
                public int IncludedCount { get; set; }

                [NotSagaState]
                public List<string> ExcludedDiagnostics { get; set; } = new();

                [CorrelationKey] public int Correlation(Started e) => e.Id;
                [Step(Order = 1)] public DoIt Step1(Started e) => new(e.Id);
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void Saga_With_MultipleFields_EmitsCorrectFieldOrder()
    {
        var src = Header + """

            namespace Sample;

            public sealed record Started(int Id) : INotification;
            public sealed record DoIt(int Id) : IRequest;

            [Saga]
            public partial class MultiFieldOrderSaga
            {
                public int A { get; set; }
                public string B { get; set; } = "";
                public bool C { get; set; }
                public System.Guid D { get; set; }
                public decimal E { get; set; }

                [CorrelationKey] public int Correlation(Started e) => e.Id;
                [Step(Order = 1)] public DoIt Step1(Started e) => new(e.Id);
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void TwoSagasInSameProject()
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
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void SameNamedEvents_InOneSaga()
    {
        // A.Placed and B.Placed, and A.Failed and B.Failed, share simple names, so their handler
        // classes and FSM triggers are namespace-qualified. Shipped is unique in the saga and
        // keeps its simple name, #216.
        var src = Header + """

            namespace A
            {
                public sealed record Placed(int Id) : INotification;
                public sealed record Failed(int Id) : INotification;
            }

            namespace B
            {
                public sealed record Placed(int Id) : INotification;
                public sealed record Failed(int Id) : INotification;
            }

            namespace App
            {
                public sealed record Shipped(int Id) : INotification;

                public sealed record Step1(int Id) : IRequest;
                public sealed record Step2(int Id) : IRequest;
                public sealed record Step3(int Id) : IRequest;
                public sealed record Undo1(int Id) : IRequest;
                public sealed record Undo2(int Id) : IRequest;

                [Saga]
                public partial class OrderSaga
                {
                    public int Id { get; set; }

                    [CorrelationKey] public int Key(A.Placed e) => e.Id;
                    [CorrelationKey] public int Key(B.Placed e) => e.Id;
                    [CorrelationKey] public int Key(A.Failed e) => e.Id;
                    [CorrelationKey] public int Key(B.Failed e) => e.Id;
                    [CorrelationKey] public int Key(Shipped e) => e.Id;

                    [Step(Order = 1, Compensate = nameof(UndoFirst), CompensateOn = typeof(A.Failed))]
                    public Step1 First(A.Placed e) { Id = e.Id; return new(e.Id); }

                    [Step(Order = 2, Compensate = nameof(UndoSecond), CompensateOn = typeof(B.Failed))]
                    public Step2 Second(B.Placed e) => new(Id);

                    [Step(Order = 3)]
                    public Step3 Third(Shipped e) => new(Id);

                    public Undo1 UndoFirst() => new(Id);
                    public Undo2 UndoSecond() => new(Id);
                }
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }

    [Fact]
    public void ReservedTriggerNames_InOneSaga()
    {
        // Complete and CompensateDone are the FSM's built-in triggers, so events with those
        // simple names get qualified handler and trigger names. The Complete event is in the
        // global namespace, so its name gets a global_ prefix. Paid keeps its simple name, #219.
        var src = """
            using System;
            using ZeroAlloc.Mediator;
            using ZeroAlloc.Saga;

            public sealed record Complete(int Id) : INotification;

            namespace App
            {
                public sealed record CompensateDone(int Id) : INotification;
                public sealed record Paid(int Id) : INotification;

                public sealed record Step1(int Id) : IRequest;
                public sealed record Step2(int Id) : IRequest;
                public sealed record Undo1(int Id) : IRequest;

                [Saga]
                public partial class OrderSaga
                {
                    public int Id { get; set; }

                    [CorrelationKey] public int Key(global::Complete e) => e.Id;
                    [CorrelationKey] public int Key(CompensateDone e) => e.Id;
                    [CorrelationKey] public int Key(Paid e) => e.Id;

                    [Step(Order = 1, Compensate = nameof(Undo), CompensateOn = typeof(CompensateDone))]
                    public Step1 First(global::Complete e) { Id = e.Id; return new(e.Id); }

                    [Step(Order = 2)]
                    public Step2 Second(Paid e) => new(Id);

                    public Undo1 Undo() => new(Id);
                }
            }
            """;
        GeneratorSnapshot.Verify(GeneratorTestHost.Run(src));
    }
}
