using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Saga.Resilience;

/// <summary>
/// Builder-side wiring for the resilience bridge. <see cref="WithResilience"/> decorates
/// the currently-registered <see cref="ISagaCommandDispatcher"/> with
/// <see cref="ResilientSagaCommandDispatcher"/> using the supplied
/// <see cref="SagaResilienceOptions"/>.
/// </summary>
public static class SagaResilienceBuilderExtensions
{
    /// <summary>
    /// Decorates the current <see cref="ISagaCommandDispatcher"/> registration with
    /// <see cref="ResilientSagaCommandDispatcher"/>. Order matters: call this AFTER
    /// any other dispatcher-replacing extension (e.g. <c>WithOutbox()</c>) so the
    /// resilience layer sits outermost.
    /// </summary>
    /// <param name="builder">The saga builder.</param>
    /// <param name="configure">Configures retry / timeout / circuit-breaker / rate-limit policies.</param>
    /// <remarks>
    /// Composition note for users of <c>ZeroAlloc.Saga.Outbox</c>: with the outbox bridge,
    /// the dispatcher's call site is the deferred-enqueue path, which rarely sees transient
    /// failures (the actual delivery happens later inside <c>OutboxSagaCommandPoller</c>).
    /// In that case, prefer configuring <c>OutboxSagaPollerOptions</c>'s retry/dead-letter
    /// for receiver-side resilience. <see cref="WithResilience"/> still works after
    /// <c>WithOutbox()</c> — it wraps the enqueue, which catches the rare local failures
    /// (serialiser throw, DI miss) — but its primary value is on the no-outbox path.
    /// </remarks>
    public static ISagaBuilder WithResilience(this ISagaBuilder builder, Action<SagaResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SagaResilienceOptions();
        configure(options);

        var services = builder.Services;

        // Locate and remove the current ISagaCommandDispatcher registration.
        // The decorator captures the inner factory and is registered with the
        // same lifetime so DI scope semantics are preserved.
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(ISagaCommandDispatcher))
            ?? throw new InvalidOperationException(
                "WithResilience(): no ISagaCommandDispatcher is currently registered. Call AddOrderFulfillmentSaga() (or your generator-emitted Add{Saga}Saga()) BEFORE WithResilience().");

        services.Remove(existing);

        // Re-register: build the inner using the original descriptor's factory/type/instance,
        // then wrap with ResilientSagaCommandDispatcher.
        services.Add(new ServiceDescriptor(
            typeof(ISagaCommandDispatcher),
            sp =>
            {
                var inner = ResolveInner(existing, sp);
                return new ResilientSagaCommandDispatcher(inner, options);
            },
            existing.Lifetime));

        return builder;
    }

    private static ISagaCommandDispatcher ResolveInner(ServiceDescriptor descriptor, IServiceProvider sp)
    {
        if (descriptor.ImplementationInstance is ISagaCommandDispatcher instance)
            return instance;
        if (descriptor.ImplementationFactory is { } factory)
            return (ISagaCommandDispatcher)factory(sp);
        if (descriptor.ImplementationType is { } type)
            return (ISagaCommandDispatcher)ActivatorUtilities.CreateInstance(sp, type);
        throw new InvalidOperationException(
            "WithResilience(): the ISagaCommandDispatcher registration has no instance, factory, or type — cannot decorate.");
    }
}
