using System;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Saga.EfCore;
using ZeroAlloc.Saga.Orm;
using ZeroAlloc.Saga.Outbox.Tests.Fixtures;
using ZeroAlloc.Saga.Redis;

namespace ZeroAlloc.Saga.Outbox.Tests;

/// <summary>
/// Which saga store <c>WithOutbox()</c> can pair with which outbox store, #199. The EF Core
/// outbox store only stages each saga command in its scoped <see cref="DbContext"/>, and only the
/// EF Core saga store on that same context saves it. Any other saga store loses every command,
/// so the host must not start. Every supported pairing must still start.
/// </summary>
/// <remarks>
/// The check reads registrations and resolves stores, but never touches a database, so the
/// ORM and Redis stores here have no schema and no server behind them.
/// </remarks>
[Collection(SagaStoreRegistrarCollection.Name)]
public sealed class StorePairingTests
{
    private const string Prefix = "ZeroAlloc.Saga.Outbox.WithOutbox(): ";

    private static IHost BuildHost(SqliteFixture fx, Action<IServiceCollection> configure)
    {
        SagaStoreRegistrar.Reset();
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDbContext<OutboxE2EDbContext>(o => o.UseSqlite(fx.Connection));
                // Never opened: the check does not query, and the worker's failed polls are
                // logged and retried until the test stops the host.
                services.AddScoped<IAsyncDbConnection>(_ => new SqliteConnection("Data Source=:memory:").AsAsync());
                configure(services);
            })
            .Build();
    }

    private static void AddEfCoreOutbox(IServiceCollection services)
        => services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
            .WithEfCore<OutboxE2EDbContext>();

    private static void AddOrmOutbox(IServiceCollection services)
        => services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50)).WithOrm();

    private static async Task<InvalidOperationException> AssertStartFailsAsync(IHost host)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync()).ConfigureAwait(false);
        Assert.StartsWith(Prefix, ex.Message, StringComparison.Ordinal);
        Assert.Contains("EfCoreOutboxStore<OutboxE2EDbContext>", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(SagaOutboxStartupCheck.SupportedPairings, ex.Message, StringComparison.Ordinal);
        return ex;
    }

    private static async Task AssertStartsAsync(IHost host)
    {
        await host.StartAsync().ConfigureAwait(false);
        await host.StopAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task InMemorySagaStore_With_EfCoreOutboxStore_Fails_At_Start()
    {
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddEfCoreOutbox(services);
            services.AddSaga().WithOutbox().WithOrderFulfillmentSaga();
        });

        var ex = await AssertStartFailsAsync(host);

        Assert.Contains("the InMemory saga store", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrmSagaStore_With_EfCoreOutboxStore_Fails_At_Start()
    {
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddEfCoreOutbox(services);
            services.AddSaga().WithOrmStore().WithOutbox().WithOrderFulfillmentSaga();
        });

        var ex = await AssertStartFailsAsync(host);

        Assert.Contains("WithOrmStore()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedisSagaStore_Without_WithRedisOutbox_With_EfCoreOutboxStore_Fails_At_Start()
    {
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddEfCoreOutbox(services);
            services.AddSaga().WithRedisStore().WithOutbox().WithOrderFulfillmentSaga();
        });

        var ex = await AssertStartFailsAsync(host);

        Assert.Contains("WithRedisStore()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EfCoreSagaStore_On_Another_DbContext_Than_The_EfCoreOutboxStore_Fails_At_Start()
    {
        // Two contexts in one scope are two change trackers: the saga store saves its own and
        // never the one the outbox store staged the command in.
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            services.AddDbContext<OtherSagaDbContext>(o => o.UseSqlite(fx.Connection));
            AddEfCoreOutbox(services);
            services.AddSaga().WithEfCoreStore<OtherSagaDbContext>().WithOutbox().WithOrderFulfillmentSaga();
        });

        var ex = await AssertStartFailsAsync(host);

        Assert.Contains("WithEfCoreStore<OtherSagaDbContext>()", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EfCoreSagaStore_With_EfCoreOutboxStore_On_The_Same_DbContext_Starts(bool outboxFirst)
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        using var host = BuildHost(fx, services =>
        {
            if (outboxFirst) AddEfCoreOutbox(services);
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga();
            if (!outboxFirst) AddEfCoreOutbox(services);
        });

        await AssertStartsAsync(host);
    }

    [Fact]
    public async Task OrmSagaStore_With_OrmOutboxStore_Starts()
    {
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddOrmOutbox(services);
            services.AddSaga().WithOrmStore().WithOutbox().WithOrderFulfillmentSaga();
        });

        await AssertStartsAsync(host);
    }

    [Fact]
    public async Task RedisSagaStore_Without_WithRedisOutbox_With_OrmOutboxStore_Starts()
    {
        // At-least-once: the ORM outbox store writes each row when the step dispatches.
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddOrmOutbox(services);
            services.AddSaga().WithRedisStore().WithOutbox().WithOrderFulfillmentSaga();
        });

        await AssertStartsAsync(host);
    }

    [Fact]
    public async Task InMemorySagaStore_With_OrmOutboxStore_Starts()
    {
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddOrmOutbox(services);
            services.AddSaga().WithOutbox().WithOrderFulfillmentSaga();
        });

        await AssertStartsAsync(host);
    }

    private sealed class OtherSagaDbContext(DbContextOptions<OtherSagaDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddSagas();
    }
}
