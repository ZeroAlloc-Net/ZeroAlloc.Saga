using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;

namespace ZeroAlloc.Saga.Tests;

public class MediatorSagaCommandDispatcherTests
{
    public readonly record struct PingCmd(string Msg) : IRequest<Unit>;

    public sealed class PingHandler : IRequestHandler<PingCmd, Unit>
    {
        public static string? LastMessage { get; set; }
        public ValueTask<Unit> Handle(PingCmd request, CancellationToken ct)
        {
            LastMessage = request.Msg;
            return ValueTask.FromResult(Unit.Value);
        }
    }

    [Fact]
    public async Task DispatchAsync_ForwardsToIMediator()
    {
        PingHandler.LastMessage = null;
        var services = new ServiceCollection();
        services.AddMediator();
        using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        var dispatcher = new MediatorSagaCommandDispatcher(mediator);
        await dispatcher.DispatchAsync(new PingCmd("hello"), CancellationToken.None);

        Assert.Equal("hello", PingHandler.LastMessage);
    }
}
