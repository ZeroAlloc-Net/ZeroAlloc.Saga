using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Builder-side wiring for the outbox bridge. <see cref="WithOutbox"/> routes every saga step's
/// command through the configured <see cref="IOutboxStore"/>, and registers one
/// <see cref="IOutboxTypeDispatcher"/> per saga command type, so ZeroAlloc.Outbox's
/// <see cref="OutboxWorkerService"/> dispatches saga commands like any other outbox message.
/// </summary>
public static class SagaOutboxBuilderExtensions
{
    /// <summary>
    /// Replaces the default <see cref="ISagaCommandDispatcher"/> with
    /// <see cref="OutboxSagaCommandDispatcher"/>, so every saga step's command is written to the
    /// outbox and committed atomically with the saga state save. Registers a scoped
    /// <see cref="IOutboxTypeDispatcher"/> for each saga command type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This does not call <c>AddOutbox()</c>. Register ZeroAlloc.Outbox once yourself, before or
    /// after <c>AddSaga()</c>. Its <see cref="OutboxWorkerService"/> claims, dispatches, retries and
    /// dead-letters saga commands, configured through <see cref="OutboxOptions"/>. When the host
    /// starts, before any hosted service, a check fails the start if no
    /// <see cref="OutboxWorkerService"/> or no <see cref="IOutboxStore"/> is registered, or if
    /// another <see cref="IOutboxTypeDispatcher"/> claims a saga command's type name.
    /// </para>
    /// <para>
    /// The saga command types come from the <see cref="SagaCommandSource"/> of every assembly whose
    /// sagas are registered on the same service collection, with their generator-emitted
    /// <c>With{Saga}()</c>, before or after this call. Sagas may be split across assemblies: each
    /// command is dispatched through the source of the assembly that declares it. The start check
    /// fails when no saga is registered, or when an assembly's sagas cannot be dispatched from the
    /// outbox because it does not reference ZeroAlloc.Serialisation. A
    /// <see cref="SagaCommandRegistryDispatcher"/> registered before this call replaces the
    /// dispatch through the sources; tests use that to short-circuit it.
    /// </para>
    /// </remarks>
    public static ISagaBuilder WithOutbox(this ISagaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        // Default unit of work: passthrough to IOutboxStore.EnqueueDeferredAsync.
        // Backend extensions that ship a transactional unit of work, such as
        // ZeroAlloc.Saga.Outbox.Redis's WithRedisOutbox(), replace this later with
        // services.Replace.
        // TryAddScoped here ensures the default doesn't clobber a backend impl
        // registered via WithRedisStore() before WithOutbox().
        services.TryAddScoped<ISagaUnitOfWork, OutboxStoreSagaUnitOfWork>();
        services.Replace(ServiceDescriptor.Scoped<ISagaCommandDispatcher, OutboxSagaCommandDispatcher>());

        // Once per container. A second call would register every saga dispatcher again, and the
        // startup check would then report each type name as claimed twice.
        if (services.Any(d => d.ServiceType == typeof(SagaOutboxRegistration)))
            return builder;

        var registration = new SagaOutboxRegistration();
        services.AddSingleton(registration);
        // The default routes each type name to the source of the assembly that declares the
        // command. TryAdd, so a delegate registered before this call replaces it.
        SagaCommandRegistryDispatcher dispatch = registration.DispatchAsync;
        services.TryAddSingleton(dispatch);

        // Every assembly's sagas, including those whose With{Saga}() runs after this call.
        builder.ForEachCommandSource(source =>
        {
            foreach (var typeName in registration.Add(source))
            {
                services.AddScoped<IOutboxTypeDispatcher>(sp => new SagaCommandOutboxDispatcher(
                    typeName, sp.GetRequiredService<SagaCommandRegistryDispatcher>(), sp));
            }
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SagaOutboxStartupCheck>(
            sp => new SagaOutboxStartupCheck(sp, sp.GetRequiredService<SagaOutboxRegistration>())));
        return builder;
    }
}
