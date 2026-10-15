using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;

namespace ZeroAlloc.Saga.Tests.Fixtures;

// A saga whose state has variable-size fields, so its snapshots can be made larger than the
// scratch writer the generated Snapshot() keeps per thread. Used by SagaSnapshotBufferTests.
public sealed record PayloadReceived(OrderId OrderId) : INotification;

public sealed record AcknowledgePayloadCommand(OrderId OrderId) : IRequest;

[Saga]
public partial class PayloadSaga
{
    public OrderId OrderId { get; set; }
    public string Note { get; set; } = string.Empty;
    public byte[] Payload { get; set; } = [];

    [CorrelationKey] public OrderId Correlation(PayloadReceived e) => e.OrderId;

    [Step(Order = 1)]
    public AcknowledgePayloadCommand Acknowledge(PayloadReceived e)
    {
        OrderId = e.OrderId;
        return new AcknowledgePayloadCommand(e.OrderId);
    }
}

public sealed class AcknowledgePayloadHandler : IRequestHandler<AcknowledgePayloadCommand, Unit>
{
    public async ValueTask<Unit> Handle(AcknowledgePayloadCommand req, CancellationToken ct)
    { await CommandLedger.Current!.RecordAsync(req).ConfigureAwait(false); return Unit.Value; }
}
