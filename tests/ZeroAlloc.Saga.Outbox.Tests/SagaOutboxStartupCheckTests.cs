using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Saga.EfCore;
using ZeroAlloc.Saga.Outbox.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Tests;

/// <summary>
/// The start-time check that <c>WithOutbox()</c> registers. Outbox's worker and a store must be
/// registered, and no other dispatcher may claim a saga command's type name. None of this fails
/// at registration, so AddOutbox and AddSaga can be called in either order.
/// </summary>
[Collection(SagaStoreRegistrarCollection.Name)]
public sealed class SagaOutboxStartupCheckTests
{
    // The documented EF Core pairing, so each test fails only on what it is about. The pairing
    // itself is covered by StorePairingTests.
    private static IHost BuildHost(SqliteFixture fx, Action<IServiceCollection> configure)
    {
        SagaStoreRegistrar.Reset();
        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDbContext<OutboxE2EDbContext>(o => o.UseSqlite(fx.Connection));
                configure(services);
            })
            .Build();
    }

    private static void AddDocumentedOutbox(IServiceCollection services)
        => services.AddOutbox(o => o.PollingInterval = TimeSpan.FromMilliseconds(50))
            .WithEfCore<OutboxE2EDbContext>();

    [Fact]
    public async Task Start_Without_AddOutbox_Throws_And_Shows_The_Supported_Setup()
    {
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            services.AddScoped<IOutboxStore, EfCoreOutboxStore<OutboxE2EDbContext>>();
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("OutboxWorkerService", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            "services.AddOutbox(o => ...).WithEfCore<AppDbContext>();", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_Without_An_Outbox_Store_Throws()
    {
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            services.AddOutbox();
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("IOutboxStore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_Without_A_Registered_Saga_Throws()
    {
        // WithOutbox() dispatches the commands of the sagas registered on the same collection.
        // It no longer scans loaded assemblies, so an assembly that merely exists, such as this
        // one, registers nothing.
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddDocumentedOutbox(services);
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox();
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Equal(SagaOutboxStartupCheck.NoSagaRegisteredMessage, ex.Message);
    }

    [Fact]
    public async Task Start_With_A_Saga_Assembly_Without_Serialisation_Throws_Naming_It()
    {
        // The generator emits the registry that deserializes outbox rows only into an assembly
        // that references ZeroAlloc.Serialisation; its source then cannot dispatch serialized
        // commands, and those commands would be dead-lettered.
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddDocumentedOutbox(services);
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga().AddCommandSource(new NonSerializingSource());
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("'ZeroAlloc.Saga.Outbox.Tests'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ZeroAlloc.Serialisation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_With_Another_Dispatcher_For_A_Saga_Command_Type_Throws_Naming_The_Type()
    {
        var typeName = typeof(ReserveStockCommand).FullName!;
        await using var fx = new SqliteFixture();
        using var host = BuildHost(fx, services =>
        {
            AddDocumentedOutbox(services);
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga();
            services.AddScoped<IOutboxTypeDispatcher>(_ => new ForeignDispatcher(typeName));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(typeName, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_With_The_Documented_Setup_Succeeds_In_Either_Order(bool outboxFirst)
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        using var host = BuildHost(fx, services =>
        {
            if (outboxFirst) AddDocumentedOutbox(services);
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga();
            if (!outboxFirst) AddDocumentedOutbox(services);
        });

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Start_With_A_Factory_Registered_Worker_Succeeds()
    {
        // A worker registered through a factory has no ImplementationType, so the check has to
        // look at the hosted-service instances rather than the descriptors.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        using var host = BuildHost(fx, services =>
        {
            services.AddOptions<OutboxOptions>().Configure(o => o.PollingInterval = TimeSpan.FromMilliseconds(50));
            services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<OutboxWorkerService>(sp));
            services.AddScoped<IOutboxStore, EfCoreOutboxStore<OutboxE2EDbContext>>();
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga();
        });

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Start_With_Saga_Dispatchers_Decorated_In_Place_Does_Not_Report_A_Conflict()
    {
        // ZeroAlloc.Outbox.Telemetry's WithTelemetry replaces every IOutboxTypeDispatcher
        // descriptor in place with a decorating factory. That keeps one dispatcher per type name.
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();
        using var host = BuildHost(fx, services =>
        {
            AddDocumentedOutbox(services);
            services.AddSaga().WithEfCoreStore<OutboxE2EDbContext>().WithOutbox().WithOrderFulfillmentSaga();
            DecorateDispatchersInPlace(services);
        });

        await host.StartAsync();
        await host.StopAsync();

        await using var scope = host.Services.CreateAsyncScope();
        Assert.All(
            scope.ServiceProvider.GetServices<IOutboxTypeDispatcher>(),
            d => Assert.IsType<DecoratingDispatcher>(d));
    }

    [Fact]
    public void The_Store_Type_Names_The_Pairing_Check_Recognises_Match_The_Real_Types()
    {
        // The check cannot reference ZeroAlloc.Saga.EfCore or ZeroAlloc.Outbox.EfCore, so it
        // recognises their types by name. A rename in either must fail here, not in production.
        Assert.Equal(SagaOutboxStartupCheck.EfCoreSagaStoreOptionsTypeName, typeof(EfCoreSagaStoreOptions).FullName);
        Assert.Equal(SagaOutboxStartupCheck.EfCoreOutboxStoreTypeName, typeof(EfCoreOutboxStore<>).FullName);
        Assert.Equal(SagaOutboxStartupCheck.DbContextTypeName, typeof(DbContext).FullName);
    }

    // Mirrors OutboxTelemetryBuilderExtensions.WithTelemetry: each descriptor is swapped for a
    // factory that builds the original and wraps it, at the same index and lifetime.
    private static void DecorateDispatchersInPlace(IServiceCollection services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            var captured = services[i];
            if (captured.ServiceType != typeof(IOutboxTypeDispatcher)) continue;

            services[i] = ServiceDescriptor.Describe(
                typeof(IOutboxTypeDispatcher),
                sp => new DecoratingDispatcher(CreateFromDescriptor(captured, sp)),
                captured.Lifetime);
        }
    }

    private static IOutboxTypeDispatcher CreateFromDescriptor(ServiceDescriptor d, IServiceProvider sp)
    {
        if (d.ImplementationInstance is not null) return (IOutboxTypeDispatcher)d.ImplementationInstance;
        if (d.ImplementationFactory is not null) return (IOutboxTypeDispatcher)d.ImplementationFactory(sp);
        return (IOutboxTypeDispatcher)ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }

    private sealed class DecoratingDispatcher(IOutboxTypeDispatcher inner) : IOutboxTypeDispatcher
    {
        public string TypeName => inner.TypeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
            => inner.DispatchAsync(payload, ct);
    }

    // A source like the one the generator emits into an assembly without ZeroAlloc.Serialisation.
    private sealed class NonSerializingSource : SagaCommandSource
    {
        public override IReadOnlyList<Type> CommandTypes { get; } = [typeof(StrayCommand)];

        public override ISagaCommandDispatcher CreateDispatcher(IServiceProvider services)
            => throw new NotSupportedException();
    }

    // Not an IRequest: this project runs the Mediator generator, which would demand a handler.
    private sealed class StrayCommand;

    private sealed class ForeignDispatcher(string typeName) : IOutboxTypeDispatcher
    {
        public string TypeName { get; } = typeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => default;
    }
}
