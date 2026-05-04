using System.Threading;
using System.Threading.Tasks;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga.Tests;

public class ISagaCommandDispatcherTests
{
    public readonly record struct TestCommand(int X) : IRequest<Unit>;

    // Mediator's ZAM001 analyzer requires a registered handler for every IRequest<T> declared
    // in the project. This handler is never invoked — DispatchAsync below short-circuits.
    public sealed class TestCommandHandler : IRequestHandler<TestCommand, Unit>
    {
        public ValueTask<Unit> Handle(TestCommand request, CancellationToken cancellationToken)
            => new(Unit.Value);
    }

    [Fact]
    public async Task Default_ImplementationContract_ForwardsToCallback()
    {
        var dispatcher = new RecordingDispatcher();
        await dispatcher.DispatchAsync(new TestCommand(42), CancellationToken.None);
        Assert.Equal(42, dispatcher.LastValue);
    }

    private sealed class RecordingDispatcher : ISagaCommandDispatcher
    {
        public int LastValue { get; private set; }
        public ValueTask DispatchAsync<TCommand>(TCommand cmd, CancellationToken ct) where TCommand : IRequest<Unit>
        {
            if (cmd is TestCommand tc) LastValue = tc.X;
            return default;
        }
    }
}
