using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga.Orm;
using ZeroAlloc.Saga.Outbox.Orm.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Orm.Tests;

/// <summary>What <c>WithOrmOutbox()</c> requires of the builder, and what it registers.</summary>
[Collection(SagaStoreRegistrarCollection.Name)]
public sealed class RegistrationTests
{
    private static IServiceCollection NewServices()
    {
        SagaStoreRegistrar.Reset();
        var services = new ServiceCollection();
        services.AddMediator();
        return services;
    }

    [Fact]
    public void Without_WithOrmStore_It_Throws()
    {
        var builder = NewServices().AddSaga().WithOutbox();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.WithOrmOutbox());

        Assert.Contains("requires WithOrmStore()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Before_WithOutbox_It_Throws()
    {
        var builder = NewServices().AddSaga().WithOrmStore();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.WithOrmOutbox());

        Assert.Contains("requires WithOutbox()", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Calling_It_Twice_Registers_One_Contributor()
    {
        var services = NewServices();

        services.AddSaga().WithOrmStore().WithOutbox().WithOrmOutbox().WithOrmOutbox();

        Assert.Single(services, d => d.ServiceType == typeof(IOrmSagaTransactionContributor));
        Assert.Single(services, d => d.ServiceType == typeof(ISagaUnitOfWork));
    }

    [Fact]
    public void The_Dispatcher_And_The_Contributor_Share_One_Buffer_Per_Scope()
    {
        // The dispatcher enlists through ISagaUnitOfWork and the contributor drains the concrete
        // buffer. Two instances would leave every enlisted row in a buffer nothing writes.
        var services = NewServices();
        services.AddSaga().WithOrmStore().WithOutbox().WithOrmOutbox();
        using var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<ISagaUnitOfWork>();
        // The buffer type is internal; its registration is found by name.
        var buffer = services.Single(d => string.Equals(d.ServiceType.Name, "OrmSagaUnitOfWork", StringComparison.Ordinal)).ServiceType;

        Assert.Same(scope.ServiceProvider.GetRequiredService(buffer), uow);

        using var other = sp.CreateScope();
        Assert.NotSame(uow, other.ServiceProvider.GetRequiredService<ISagaUnitOfWork>());
    }
}
