using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

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
    /// The generator-emitted <c>ZeroAlloc.Saga.Generated.SagaCommandRegistry</c> is located by
    /// reflection when this method runs, to learn the saga command type names. A
    /// <see cref="SagaCommandRegistryDispatcher"/> registered before this call replaces the
    /// reflective dispatch; tests use that to short-circuit it.
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

        var registryFound = TryFindRegistry(out var typeNames, out var dispatch);
        if (registryFound)
        {
            services.TryAddSingleton<SagaCommandRegistryDispatcher>(dispatch!);
            foreach (var typeName in typeNames)
            {
                services.AddScoped<IOutboxTypeDispatcher>(sp => new SagaCommandOutboxDispatcher(
                    typeName, sp.GetRequiredService<SagaCommandRegistryDispatcher>(), sp));
            }
        }

        services.AddSingleton(new SagaOutboxRegistration(typeNames, registryFound));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SagaOutboxStartupCheck>(
            sp => new SagaOutboxStartupCheck(sp, sp.GetRequiredService<SagaOutboxRegistration>())));
        return builder;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "SagaCommandRegistry is rooted by [DynamicDependency(NonPublicMethods, typeof(SagaCommandRegistry))] emitted on the saga generator's MediatorSagaCommandDispatcher. That dispatcher is rooted by the generator-emitted With{Saga}Saga DI registration, transitively keeping the registry's DispatchAsync and GetTypeNames alive under PublishAot=true.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075:RequiresUnreferencedCode",
        Justification = "Same: SagaCommandRegistry's non-public methods, DispatchAsync and GetTypeNames, are kept by the [DynamicDependency] on MediatorSagaCommandDispatcher; both GetMethod lookups find them after trimming.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Reflective MethodInfo.Invoke is over non-generic static methods; no dynamic code generation needed for AOT.")]
    private static bool TryFindRegistry(
        out IReadOnlyList<string> typeNames,
        [NotNullWhen(true)] out SagaCommandRegistryDispatcher? dispatch)
    {
        // Walk the loaded assemblies to find the generator-emitted registry. Lives in
        // namespace ZeroAlloc.Saga.Generated; static methods GetTypeNames() and
        // DispatchAsync(string, ReadOnlyMemory<byte>, IServiceProvider, IMediator, CancellationToken).
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

            // NonPublic as well as Public: the registry's methods are internal, because
            // DispatchAsync takes the generator-emitted IMediator, which is internal from Mediator v5.
            var method = registryType.GetMethod(
                "DispatchAsync",
                AnyStatic,
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
            var getTypeNames = registryType.GetMethod(
                "GetTypeNames", AnyStatic, binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method is null || getTypeNames?.Invoke(null, null) is not IReadOnlyList<string> names) continue;

            typeNames = names;
            dispatch = (typeName, bytes, sp, ct) =>
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
            return true;
        }

        typeNames = Array.Empty<string>();
        dispatch = null;
        return false;
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
