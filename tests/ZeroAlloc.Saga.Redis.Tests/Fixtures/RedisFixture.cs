using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ZeroAlloc.Saga.Redis.Tests.Fixtures;

/// <summary>
/// Per-test Redis fixture backed by Testcontainers.Redis. Spins up a fresh
/// Redis 7.x container for the duration of the test, then disposes it.
/// </summary>
/// <remarks>
/// Each fixture instance owns one container + one <see cref="IConnectionMultiplexer"/>.
/// Tests that need cross-process race semantics can build two fixtures (or two
/// multiplexers against the same container) — see <c>OccTests</c> for the pattern.
/// </remarks>
public sealed class RedisFixture : IAsyncDisposable
{
    private readonly RedisContainer _container;
    public IConnectionMultiplexer Multiplexer { get; private set; } = null!;

    public RedisFixture()
    {
        _container = new RedisBuilder()
            .WithImage("redis:7.4-alpine")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        var connectionString = _container.GetConnectionString();
        // AbortOnConnectFail=false so a transient hiccup during container boot
        // doesn't sink the whole test class.
        Multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString + ",abortConnect=false")
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Multiplexer is not null) await Multiplexer.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
