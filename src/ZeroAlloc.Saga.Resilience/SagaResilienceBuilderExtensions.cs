using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    /// <summary>The type name (not a typed reference, to avoid a project dep on Saga.Outbox) of the
    /// outbox dispatcher. Used by <see cref="WithResilience"/> to detect a low-value composition
    /// shape and emit a warning.</summary>
    private const string OutboxDispatcherTypeName = "OutboxSagaCommandDispatcher";

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

        // Detect the outbox-wrap shape now (at registration) but defer the actual log call to
        // resolution time, where we can use the configured ILoggerFactory from the resolved
        // ServiceProvider. A one-shot guard prevents the warning from firing on every resolve.
        var isWrappingOutboxDispatcher = string.Equals(existing.ImplementationType?.Name, OutboxDispatcherTypeName, StringComparison.Ordinal);
        var warningFired = 0;

        // Re-register: build the inner using the original descriptor's factory/type/instance,
        // then wrap with ResilientSagaCommandDispatcher.
        services.Add(new ServiceDescriptor(
            typeof(ISagaCommandDispatcher),
            sp =>
            {
                if (isWrappingOutboxDispatcher && System.Threading.Interlocked.CompareExchange(ref warningFired, 1, 0) == 0)
                {
                    var loggerFactory = sp.GetService<ILoggerFactory>();
                    var logger = loggerFactory?.CreateLogger("ZeroAlloc.Saga.Resilience.SagaResilienceBuilderExtensions")
                        ?? (ILogger)NullLogger.Instance;
                    logger.LogWarning(
                        "WithResilience() is wrapping ZeroAlloc.Saga.Outbox.OutboxSagaCommandDispatcher. The wrap covers the deferred-enqueue path, which rarely sees transient failures. For receiver-side retry/circuit-breaker under the outbox bridge, configure OutboxSagaPollerOptions instead. See docs/resilience.md.");
                }

                var inner = ResolveInner(existing, sp);
                return new ResilientSagaCommandDispatcher(inner, options);
            },
            existing.Lifetime));

        return builder;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "ActivatorUtilities.CreateInstance receives the user's own dispatcher Type from their DI registration; the user is responsible for keeping that type rooted under PublishAot=true. If a future SDK adds RequiresUnreferencedCode/RequiresDynamicCode annotations to CreateInstance, this suppression matches the existing trim contract documented in docs/resilience.md.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Same: ActivatorUtilities.CreateInstance constructor lookup is non-generic; no dynamic code generation under AOT.")]
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
