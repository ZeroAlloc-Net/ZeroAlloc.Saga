using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ZeroAlloc.Saga.Outbox.Redis.Tests.Fixtures;

public sealed class RedisFixture : IAsyncDisposable
{
    private readonly RedisContainer _container;
    public IConnectionMultiplexer Multiplexer { get; private set; } = null!;

    public RedisFixture()
    {
        _container = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        var cs = _container.GetConnectionString();
        Multiplexer = await ConnectionMultiplexer.ConnectAsync(cs + ",abortConnect=false").ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Multiplexer is not null) await Multiplexer.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
