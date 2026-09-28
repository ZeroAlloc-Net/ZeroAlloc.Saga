using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZeroAlloc.Saga;

/// <summary>
/// Registers <see cref="SagaCommandSource"/>s, one per assembly that declares sagas, and lets
/// integrations such as the outbox bridge act on every one of them.
/// </summary>
/// <remarks>
/// Sources are registered in the service collection as <see cref="SagaCommandSource"/>
/// singletons, so the runtime enumerates exactly the sagas the application registered, whichever
/// assemblies they live in and whether or not those are loaded yet. No assembly is scanned.
/// </remarks>
public static class SagaCommandSourceBuilderExtensions
{
    /// <summary>
    /// Adds the saga command source of one assembly. Generator-emitted <c>With{Saga}()</c> calls
    /// this; adding the same source, or another instance of the same source type, again is a
    /// no-op, so every saga of an assembly can call it.
    /// </summary>
    /// <remarks>
    /// The first call also registers the default <see cref="ISagaCommandDispatcher"/>, unless one
    /// is already registered. It dispatches through the only source's own dispatcher when there is
    /// one source, and routes each command to the source that lists its type when there are more.
    /// Callbacks registered with <see cref="ForEachCommandSource"/> run for the new source.
    /// </remarks>
    /// <param name="builder">The saga builder.</param>
    /// <param name="source">The source to add.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// The source lists a command type that a source from another assembly already lists. A saga
    /// command type must belong to the sagas of one assembly: the outbox keeps one dispatcher per
    /// type name, and the default dispatcher needs a single route for it.
    /// </exception>
    public static ISagaBuilder AddCommandSource(this ISagaBuilder builder, SagaCommandSource source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        var services = builder.Services;
        var claimed = new Dictionary<Type, SagaCommandSource>();
        foreach (var existing in RegisteredSources(services))
        {
            if (ReferenceEquals(existing, source) || existing.GetType() == source.GetType())
                return builder;
            foreach (var type in existing.CommandTypes)
                claimed[type] = existing;
        }

        foreach (var type in source.CommandTypes)
        {
            if (claimed.TryGetValue(type, out var owner))
                throw new InvalidOperationException(ConflictMessage(type, owner, source));
        }

        services.AddSingleton(source);
        services.TryAddSingleton(sp => new SagaCommandRouting(sp.GetServices<SagaCommandSource>()));
        services.TryAddScoped<ISagaCommandDispatcher>(SagaCommandRouting.CreateDefaultDispatcher);

        // A snapshot: a callback may itself register another callback.
        if (Callbacks(services, create: false) is { } callbacks)
        {
            foreach (var callback in callbacks.ToArray())
                callback(source);
        }

        return builder;
    }

    /// <summary>
    /// Runs <paramref name="callback"/> for every <see cref="SagaCommandSource"/> already added to
    /// <paramref name="builder"/>'s service collection, and for every one added to it later. An
    /// integration that registers something per saga command, such as the outbox bridge, uses this
    /// so it does not depend on being called after every <c>With{Saga}()</c>.
    /// </summary>
    /// <param name="builder">The saga builder.</param>
    /// <param name="callback">Called once per source.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static ISagaBuilder ForEachCommandSource(this ISagaBuilder builder, Action<SagaCommandSource> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        var services = builder.Services;
        Callbacks(services, create: true)!.Add(callback);
        foreach (var source in RegisteredSources(services))
            callback(source);

        return builder;
    }

    internal static string NameOf(SagaCommandSource source)
        => source.GetType().Assembly.GetName().Name ?? source.GetType().FullName ?? source.GetType().Name;

    internal static string ConflictMessage(Type commandType, SagaCommandSource first, SagaCommandSource second)
        => $"Saga command type '{commandType.FullName}' is returned by sagas in two assemblies, " +
           $"'{NameOf(first)}' and '{NameOf(second)}'. A saga command type must belong to the sagas of " +
           "one assembly: the outbox keeps one dispatcher per type name, and the default dispatcher needs " +
           "a single route for it. Give each assembly's sagas their own command types.";

    private static List<SagaCommandSource> RegisteredSources(IServiceCollection services)
    {
        var sources = new List<SagaCommandSource>();
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(SagaCommandSource)
                && descriptor.ImplementationInstance is SagaCommandSource source)
            {
                sources.Add(source);
            }
        }

        return sources;
    }

    private static List<Action<SagaCommandSource>>? Callbacks(IServiceCollection services, bool create)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(SagaCommandSourceCallbacks)
                && descriptor.ImplementationInstance is SagaCommandSourceCallbacks callbacks)
            {
                return callbacks.Items;
            }
        }

        if (!create)
            return null;

        var created = new SagaCommandSourceCallbacks();
        services.AddSingleton(created);
        return created.Items;
    }

    /// <summary>
    /// Builder-time state: the <see cref="ForEachCommandSource"/> callbacks of one service
    /// collection. Kept in the collection itself so every <see cref="ISagaBuilder"/> over it shares
    /// them.
    /// </summary>
    private sealed class SagaCommandSourceCallbacks
    {
        public List<Action<SagaCommandSource>> Items { get; } = [];
    }
}
