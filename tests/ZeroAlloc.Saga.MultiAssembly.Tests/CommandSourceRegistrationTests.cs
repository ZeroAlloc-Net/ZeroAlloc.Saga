using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Outbox;
using ZeroAlloc.Saga.MultiAssembly.Billing;
using ZeroAlloc.Saga.MultiAssembly.Shipping;
using ZeroAlloc.Saga.Outbox;

namespace ZeroAlloc.Saga.MultiAssembly.Tests;

/// <summary>
/// How the generated <see cref="SagaCommandSource"/>s of two assemblies are registered and
/// composed, #176.
/// </summary>
public sealed class CommandSourceRegistrationTests
{
    // Ordinal order, as the assertions sort what they read.
    private static readonly string[] AllCommandTypeNames =
    [
        typeof(CloseInvoiceCommand).FullName!,
        typeof(IssueInvoiceCommand).FullName!,
        typeof(BookCarrierCommand).FullName!,
        typeof(CloseShipmentCommand).FullName!,
    ];

    [Fact]
    public void EachAssembly_RegistersOneSource_HoweverOftenItsSagasAreAdded()
    {
        var services = new ServiceCollection();
        services.AddSaga().WithInvoiceSaga().WithShipmentSaga().WithInvoiceSaga();

        var sources = services
            .Where(d => d.ServiceType == typeof(SagaCommandSource))
            .Select(d => Assert.IsAssignableFrom<SagaCommandSource>(d.ImplementationInstance))
            .ToList();

        Assert.Equal(2, sources.Count);
        Assert.Equal(
            AllCommandTypeNames,
            sources.SelectMany(s => s.CommandTypes).Select(t => t.FullName!).Order(StringComparer.Ordinal),
            StringComparer.Ordinal);
        Assert.All(sources, s => Assert.True(s.CanDispatchSerialized));
    }

    [Fact]
    public async Task SingleAssembly_DefaultDispatcher_IsThatAssemblysOwnDispatcher()
    {
        // One assembly behaves exactly as before sources existed: no routing layer.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSaga().WithInvoiceSaga();

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();
        Assert.Equal("ZeroAlloc.Saga.Generated.MediatorSagaCommandDispatcher", dispatcher.GetType().FullName);
        Assert.Equal("ZeroAlloc.Saga.MultiAssembly.Billing", dispatcher.GetType().Assembly.GetName().Name);
    }

    [Fact]
    public async Task RoutingDispatcher_RejectsACommandNoRegisteredSagaReturns()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSaga().WithInvoiceSaga().WithShipmentSaga();

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ISagaCommandDispatcher>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.DispatchAsync(new StrayCommand(), CancellationToken.None).AsTask());

        Assert.Contains(typeof(StrayCommand).FullName!, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACommandTypeClaimedByTwoAssemblies_FailsNamingBoth(bool generatedFirst)
    {
        // This test assembly's source claims Billing's IssueInvoiceCommand. The outbox keeps one
        // dispatcher per type name, so this has to fail at registration, whichever comes first.
        var builder = new ServiceCollection().AddSaga();
        if (generatedFirst) builder.WithInvoiceSaga();
        else builder.AddCommandSource(new ClaimsIssueInvoiceSource());

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            if (generatedFirst) builder.AddCommandSource(new ClaimsIssueInvoiceSource());
            else builder.WithInvoiceSaga();
        });

        Assert.Contains(typeof(IssueInvoiceCommand).FullName!, ex.Message, StringComparison.Ordinal);
        Assert.Contains("'ZeroAlloc.Saga.MultiAssembly.Billing'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'ZeroAlloc.Saga.MultiAssembly.Tests'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithOutbox_RegistersADispatcherForEveryCommandOfBothAssemblies(bool outboxFirst)
    {
        var services = new ServiceCollection();
        var builder = services.AddSaga();
        if (outboxFirst)
            builder.WithOutbox().WithInvoiceSaga().WithShipmentSaga();
        else
            builder.WithInvoiceSaga().WithShipmentSaga().WithOutbox();

        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var names = scope.ServiceProvider.GetServices<IOutboxTypeDispatcher>()
            .Select(d => d.TypeName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(AllCommandTypeNames, names, StringComparer.Ordinal);
        // A With{Saga}() after WithOutbox() must not bring the default dispatcher back.
        Assert.Equal(
            typeof(OutboxSagaCommandDispatcher),
            Assert.Single(services, d => d.ServiceType == typeof(ISagaCommandDispatcher)).ImplementationType);
    }

    [Fact]
    public void ForEachCommandSource_SeesSourcesAddedBeforeAndAfterIt()
    {
        var seen = new List<SagaCommandSource>();
        var services = new ServiceCollection();
        services.AddSaga().WithInvoiceSaga().ForEachCommandSource(seen.Add).WithShipmentSaga().WithInvoiceSaga();

        Assert.Equal(
            ["ZeroAlloc.Saga.MultiAssembly.Billing", "ZeroAlloc.Saga.MultiAssembly.Shipping"],
            seen.Select(s => s.GetType().Assembly.GetName().Name!),
            StringComparer.Ordinal);
    }

    private sealed record StrayCommand : IRequest;

    private sealed class ClaimsIssueInvoiceSource : SagaCommandSource
    {
        public override IReadOnlyList<Type> CommandTypes { get; } = [typeof(IssueInvoiceCommand)];

        public override ISagaCommandDispatcher CreateDispatcher(IServiceProvider services)
            => throw new NotSupportedException();
    }
}
