using System;
using System.Diagnostics.CodeAnalysis;

namespace ZeroAlloc.Saga;

/// <summary>
/// Indirection point that lets backend packages (Saga.EfCore, Saga.Redis, …)
/// install their concrete <see cref="ISagaStore{TSaga,TKey}"/> registration
/// without forcing the generator-emitted <c>AddXxxSaga()</c> code to reference
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

    /// <summary>
    /// Installs the registrar delegate. Called by backend packages from
    /// inside their <c>WithEfCoreStore&lt;TContext&gt;</c> (etc.) extensions.
    /// The delegate receives the builder and is responsible for adding
    /// closed-generic <see cref="ISagaStore{TSaga,TKey}"/> registrations
    /// for every saga the user has wired up.
    /// </summary>
    /// <remarks>
    /// In practice the delegate is a small closure that calls
    /// <see cref="OverrideStore{TSaga,TKey,TStore}"/> per known saga; backend
    /// packages typically expose a per-saga registration method that closes
    /// over the saga and key types.
    /// </remarks>
    public static void SetRegistrar(System.Action<ISagaBuilder> registrar)
    {
        _registrar = registrar ?? throw new System.ArgumentNullException(nameof(registrar));
    }

    /// <summary>
    /// Invoked by generator-emitted <c>AddXxxSaga()</c> when a durable backend
    /// is wired. Forwards to the registrar so the backend can swap the
    /// default <c>InMemorySagaStore</c> registration for its concrete
    /// implementation.
    /// </summary>
    public static void Apply<TSaga, TKey>(ISagaBuilder builder)
        where TSaga : class, new()
        where TKey : notnull, System.IEquatable<TKey>
    {
        if (_registrar is null)
        {
            throw new System.InvalidOperationException(
                "ISagaBuilder.IsEfCoreBackend was set, but no SagaStoreRegistrar has been installed. " +
                "Ensure the corresponding backend package (e.g. ZeroAlloc.Saga.EfCore) is referenced and " +
                "WithEfCoreStore<TContext>() is called before any AddXxxSaga() registrations.");
        }
        _registrar(builder);
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
