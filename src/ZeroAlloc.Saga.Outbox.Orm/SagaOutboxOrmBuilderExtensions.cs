using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Saga.Orm;

namespace ZeroAlloc.Saga.Outbox.Orm;

/// <summary>
/// Makes outbox dispatch atomic on the ORM saga store: a saga step's outbox rows commit in the
/// same database transaction as the saga row.
/// </summary>
public static class SagaOutboxOrmBuilderExtensions
{
    /// <summary>
    /// Commits every saga step's outbox rows in the ORM saga store's transaction. Call it after
    /// <c>WithOrmStore()</c> and <c>WithOutbox()</c>, and register ZeroAlloc.Outbox's ORM store
    /// with <c>services.AddOutbox().WithOrm()</c>.
    /// </summary>
    /// <param name="builder">The saga builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <c>WithOrmStore()</c> or <c>WithOutbox()</c> has not been called on this builder yet.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This replaces the unit of work <c>WithOutbox()</c> registers. That default hands each row
    /// to the outbox store as soon as the step dispatches, and the ORM outbox store writes it
    /// immediately, so a save that then loses its concurrency check leaves the row behind and the
    /// retry enqueues the command again. Here the rows are buffered for the scope instead, and the
    /// saga store's transaction writes them after its own insert, update or delete, through
    /// <c>OrmOutboxStore.EnqueueInTransactionAsync</c>. A conflict or any other failure rolls back
    /// the saga row and the outbox rows together. A removal commits its rows even when there is no
    /// saga row, as for a saga that one event both starts and completes.
    /// </para>
    /// <para>
    /// The outbox rows are written on the saga store's connection, so the <c>OutboxMessages</c>
    /// table must live in the same database as the <c>SagaInstance</c> table. When the host
    /// starts, a check fails the start if the <c>IOutboxStore</c> the outbox worker claims from is
    /// not the ORM outbox store.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddOutbox().WithOrm(OutboxOrmDialect.SqlServer);
    /// services.AddSaga()
    ///         .WithOrmStore()
    ///         .WithOutbox()
    ///         .WithOrmOutbox()
    ///         .WithOrderFulfillmentSaga();
    /// </code>
    /// </example>
    public static ISagaBuilder WithOrmOutbox(this ISagaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        // The rows are written inside the ORM saga store's transaction, so that store has to be
        // the one configured. OrmSagaStoreOptions is registered by WithOrmStore() alone.
        if (!services.Any(d => d.ServiceType == typeof(OrmSagaStoreOptions)))
        {
            throw new InvalidOperationException(
                "WithOrmOutbox() requires WithOrmStore() to be configured first. The ORM outbox unit of " +
                "work writes saga commands inside the ORM saga store's transaction; without WithOrmStore() " +
                "there is no such transaction to write into.");
        }

        // WithOutbox() is what routes saga commands to the unit of work at all.
        if (!services.Any(d => d.ServiceType == typeof(ISagaCommandDispatcher)
                && d.ImplementationType == typeof(OutboxSagaCommandDispatcher)))
        {
            throw new InvalidOperationException(
                "WithOrmOutbox() requires WithOutbox() to be called first. WithOutbox() sends each saga " +
                "command to the outbox; WithOrmOutbox() then makes those writes commit with the saga state " +
                "(e.g. services.AddSaga().WithOrmStore().WithOutbox().WithOrmOutbox()).");
        }

        // Once per container: a second contributor would find the buffer already drained, but
        // there is no reason to register everything twice.
        if (services.Any(d => d.ServiceType == typeof(OrmSagaUnitOfWork)))
            return builder;

        // The dispatcher resolves ISagaUnitOfWork to enlist and the contributor resolves the
        // concrete type to drain. Both must be the same scoped instance, or the enlisted rows land
        // in a buffer nothing ever writes. So the concrete type is registered once and
        // ISagaUnitOfWork aliases it.
        services.AddScoped<OrmSagaUnitOfWork>();
        services.Replace(ServiceDescriptor.Scoped<ISagaUnitOfWork>(sp => sp.GetRequiredService<OrmSagaUnitOfWork>()));
        services.AddScoped<IOrmSagaTransactionContributor>(sp =>
            new OrmOutboxTransactionContributor(sp.GetRequiredService<OrmSagaUnitOfWork>(), sp));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OrmOutboxStartupCheck>(
            sp => new OrmOutboxStartupCheck(sp)));

        return builder;
    }
}
