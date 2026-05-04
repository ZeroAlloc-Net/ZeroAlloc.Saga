using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Builder-side wiring for the outbox bridge. <see cref="WithOutbox"/> swaps the default
/// <see cref="ISagaCommandDispatcher"/> registration with <see cref="OutboxSagaCommandDispatcher"/>
/// and registers <see cref="OutboxSagaCommandPoller"/> as a hosted service so enqueued
/// commands are picked up and dispatched through the saga's mediator.
/// </summary>
public static class SagaOutboxBuilderExtensions
{
    /// <summary>
    /// Replaces the default <see cref="ISagaCommandDispatcher"/> with
    /// <see cref="OutboxSagaCommandDispatcher"/> and registers
    /// <see cref="OutboxSagaCommandPoller"/> as a hosted service. After this call,
    /// every saga step's command dispatch is routed through the configured
    /// <see cref="ZeroAlloc.Outbox.IOutboxStore"/> and committed atomically with
    /// the saga state save.
    /// </summary>
    /// <remarks>
    /// The <see cref="SagaCommandRegistryDispatcher"/> is registered lazily — its
    /// reflective lookup of the generator-emitted
    /// <c>ZeroAlloc.Saga.Generated.SagaCommandRegistry</c> only runs the first time
    /// the poller resolves it. Tests can pre-register a fake
    /// <see cref="SagaCommandRegistryDispatcher"/> on the service collection to
    /// short-circuit the lookup.
    /// </remarks>
    public static ISagaBuilder WithOutbox(this ISagaBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        // Default unit of work: passthrough to IOutboxStore.EnqueueDeferredAsync.
        // Backend extensions that ship a transactional unit-of-work
        // (e.g. ZeroAlloc.Saga.Redis's RedisSagaUnitOfWork) override this with
        // services.Replace(...) so their MULTI/EXEC-based atomicity wins.
        // TryAddScoped here ensures the default doesn't clobber a backend impl
        // registered via WithRedisStore() before WithOutbox().
        services.TryAddScoped<ISagaUnitOfWork, OutboxStoreSagaUnitOfWork>();
        services.Replace(ServiceDescriptor.Scoped<ISagaCommandDispatcher, OutboxSagaCommandDispatcher>());
        services.TryAddSingleton<SagaCommandRegistryDispatcher>(_ => CreateRegistryDispatcher());
        services.AddHostedService<OutboxSagaCommandPoller>();
        return builder;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "SagaCommandRegistry is rooted by [DynamicDependency(PublicMethods, typeof(SagaCommandRegistry))] emitted on the saga generator's MediatorSagaCommandDispatcher. That dispatcher is rooted by the generator-emitted With{Saga}Saga DI registration, transitively keeping the registry's DispatchAsync alive under PublishAot=true.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:RequiresUnreferencedCode",
        Justification = "Same: SagaCommandRegistry's public methods are kept by the [DynamicDependency] on MediatorSagaCommandDispatcher; the GetMethod lookup will find DispatchAsync after trimming.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Reflective MethodInfo.Invoke is over a non-generic static method; no dynamic code generation needed for AOT.")]
    private static SagaCommandRegistryDispatcher CreateRegistryDispatcher()
    {
        // Walk the loaded assemblies to find the generator-emitted registry. Lives in
        // namespace ZeroAlloc.Saga.Generated; static method DispatchAsync(string,
        // ReadOnlyMemory<byte>, IServiceProvider, IMediator, CancellationToken).
        // IMediator is also generator-emitted in the consumer compilation, so we
        // resolve it via IServiceProvider and pass it along.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var registryType = asm.GetType("ZeroAlloc.Saga.Generated.SagaCommandRegistry", throwOnError: false);
            if (registryType is null) continue;

            // Resolve the IMediator type from the same assembly as the registry — it lives
            // in namespace ZeroAlloc.Mediator and is generator-emitted in the consumer.
            var mediatorType = asm.GetType("ZeroAlloc.Mediator.IMediator", throwOnError: false)
                ?? FindIMediatorType();
            if (mediatorType is null) continue;

            var method = registryType.GetMethod(
                "DispatchAsync",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[]
                {
                    typeof(string),
                    typeof(ReadOnlyMemory<byte>),
                    typeof(IServiceProvider),
                    mediatorType,
                    typeof(CancellationToken),
                },
                modifiers: null);
            if (method is null) continue;

            return (typeName, bytes, sp, ct) =>
            {
                var mediator = sp.GetService(mediatorType);
                if (mediator is null)
                {
                    throw new InvalidOperationException(
                        $"WithOutbox(): no service registered for the generator-emitted {mediatorType.FullName}. Did you call AddMediator()?");
                }
                var result = method.Invoke(null, new[] { typeName, (object)bytes, sp, mediator, ct });
                return result is ValueTask vt ? vt : default;
            };
        }

        throw new InvalidOperationException(
            "ZeroAlloc.Saga.Outbox.WithOutbox(): could not locate the generator-emitted ZeroAlloc.Saga.Generated.SagaCommandRegistry. " +
            "Ensure ZeroAlloc.Saga 1.2+ is referenced AND at least one [Saga]-decorated class exists in the compilation, AND ZeroAlloc.Serialisation is referenced.");
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Walks loaded assemblies looking for the generator-emitted IMediator; types are rooted by the consumer's Mediator generator.")]
    private static Type? FindIMediatorType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("ZeroAlloc.Mediator.IMediator", throwOnError: false);
            if (t is not null) return t;
        }
        return null;
    }
}
