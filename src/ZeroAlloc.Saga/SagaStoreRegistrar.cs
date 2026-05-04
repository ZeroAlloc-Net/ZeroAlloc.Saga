using System;
using System.Diagnostics.CodeAnalysis;

namespace ZeroAlloc.Saga;

/// <summary>
/// Indirection point that lets backend packages (Saga.EfCore, Saga.Redis, …)
/// install their concrete <see cref="ISagaStore{TSaga,TKey}"/> registration
/// without forcing the generator-emitted <c>WithXxxSaga()</c> code to reference
/// backend-specific types.
/// </summary>
/// <remarks>
/// <para>
/// Generator-emitted code calls <see cref="Apply{TSaga,TKey}"/> when
/// <see cref="ISagaBuilder.IsEfCoreBackend"/> is true. The default
/// implementation throws — the backend package must call
/// <see cref="SetRegistrar"/> from inside its <c>WithEfCoreStore&lt;TContext&gt;()</c>
/// extension to install a closed-generic registration delegate. Both
/// generator emit and backend install paths use closed-generic types only,
/// so AOT is preserved.
/// </para>
/// <para>
/// The registrar is process-wide (no static-state-per-IServiceCollection),
/// matching the v1.0 contract that DI registration is composed once at app
/// startup. Calling <c>WithEfCoreStore</c> from a different package (e.g. an
/// integration test that uses a different host) overrides the registrar.
/// </para>
/// </remarks>
public static class SagaStoreRegistrar
{
    private static System.Action<ISagaBuilder>? _registrar;
    private static ISagaStoreRegistrar? _typedRegistrar;

    /// <summary>
    /// Installs the legacy <see cref="System.Action{T}"/> registrar delegate.
    /// Retained for compatibility with the Saga 1.1.0 contract; backend
    /// packages should prefer <see cref="SetTypedRegistrar(ISagaStoreRegistrar)"/>
    /// because it forwards <c>TSaga</c>/<c>TKey</c> to the registrar so
    /// per-saga closed-generic registration works without ambient state.
    /// </summary>
    public static void SetRegistrar(System.Action<ISagaBuilder> registrar)
    {
        _registrar = registrar ?? throw new System.ArgumentNullException(nameof(registrar));
    }

    /// <summary>
    /// Installs a typed registrar that participates in per-saga
    /// closed-generic registration. Backend packages call this from
    /// inside their <c>WithEfCoreStore&lt;TContext&gt;</c> (etc.) extensions
    /// so that generator-emitted <c>WithXxxSaga()</c> can dispatch to a
    /// strongly-typed <see cref="ISagaStoreRegistrar.Register{TSaga,TKey}"/>
    /// hook with full type information.
    /// </summary>
    /// <remarks>
    /// Setting the typed registrar takes precedence over the legacy
    /// <see cref="SetRegistrar(System.Action{ISagaBuilder})"/> delegate when both are
    /// installed. AOT-safe: <see cref="ISagaStoreRegistrar.Register{TSaga,TKey}"/>
    /// is a generic interface method dispatched by the runtime without
    /// reflection or <c>MakeGenericType</c>.
    /// </remarks>
    public static void SetTypedRegistrar(ISagaStoreRegistrar registrar)
    {
        _typedRegistrar = registrar ?? throw new System.ArgumentNullException(nameof(registrar));
    }

    /// <summary>
    /// Test-only reset of installed registrars. Backend integration tests
    /// that construct multiple <see cref="System.IServiceProvider"/>s may invoke
    /// this between cases to keep the process-wide state predictable.
    /// </summary>
    public static void Reset()
    {
        _registrar = null;
        _typedRegistrar = null;
    }

    /// <summary>
    /// Invoked by generator-emitted <c>WithXxxSaga()</c>. If a backend package
    /// (Saga.EfCore, Saga.Redis, …) has installed a registrar via
    /// <see cref="SetTypedRegistrar"/> or <see cref="SetRegistrar"/>, forwards to it
    /// so the backend can swap the default <c>InMemorySagaStore</c> registration.
    /// </summary>
    /// <remarks>
    /// Returns silently when no registrar is installed — the InMemory default stays
    /// in place. This makes the generator's <c>WithXxxSaga()</c> emit independent
    /// of which backends exist (an unconditional <see cref="Apply{TSaga,TKey}"/>
    /// call lets any future backend plug in via <see cref="SetTypedRegistrar"/>
    /// without a generator update).
    /// </remarks>
    public static void Apply<TSaga, TKey>(ISagaBuilder builder)
        where TSaga : class, new()
        where TKey : notnull, System.IEquatable<TKey>
    {
        if (_typedRegistrar is not null)
        {
            _typedRegistrar.Register<TSaga, TKey>(builder);
            return;
        }
        if (_registrar is not null)
        {
            _registrar(builder);
            return;
        }
        // No backend installed → the InMemory default registration emitted by
        // WithXxxSaga() upstream is the final binding. No-op.
    }

    /// <summary>
    /// Helper used by backend registrars to swap a closed-generic
    /// <see cref="ISagaStore{TSaga,TKey}"/> registration. Removes the default
    /// in-memory entry (added by generator emit) and adds the backend's
    /// scoped concrete store.
    /// </summary>
    public static void OverrideStore<
        TSaga,
        TKey,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(ISagaBuilder builder)
        where TSaga : class, new()
        where TKey : notnull, System.IEquatable<TKey>
        where TStore : class, ISagaStore<TSaga, TKey>
    {
        if (builder is null) throw new System.ArgumentNullException(nameof(builder));
        var services = builder.Services;
        // Strip any existing registrations (the InMemory one emitted upstream).
        for (int i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(ISagaStore<TSaga, TKey>))
            {
                services.RemoveAt(i);
            }
        }
        services.Add(new Microsoft.Extensions.DependencyInjection.ServiceDescriptor(
            typeof(ISagaStore<TSaga, TKey>),
            typeof(TStore),
            Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped));
    }
}
