using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Fails the host start when saga commands written to the outbox would never be dispatched.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SagaOutboxBuilderExtensions.WithOutbox"/> cannot check this when it runs, because
/// the application may call <c>AddOutbox()</c> after it. The check runs in
/// <see cref="StartingAsync"/>, which the host calls on every <see cref="IHostedLifecycleService"/>
/// before it starts any hosted service, so Outbox's worker never claims a saga command before the
/// check has passed.
/// </para>
/// <para>
/// It throws when no saga is registered, when an assembly's sagas cannot be dispatched from the
/// outbox because it does not reference ZeroAlloc.Serialisation, when no
/// <see cref="OutboxWorkerService"/> is registered, when no <see cref="IOutboxStore"/> resolves,
/// when the saga store cannot commit what the outbox store stages, when another
/// <see cref="IOutboxTypeDispatcher"/> claims a saga command's type name, or when a saga command
/// type has no <c>ISerializer&lt;T&gt;</c>. The worker keeps one dispatcher per type name, the
/// last one registered, so the other one would never run. A missing serializer would otherwise
/// fail only when a saga first dispatches that command, which for a compensation command may be
/// long after the host started.
/// </para>
/// <para>
/// The store pairing matters because of <see cref="OutboxStoreSagaUnitOfWork"/>, the unit of work
/// <c>WithOutbox()</c> registers unless a backend replaces it. It enlists each saga command
/// through <see cref="IOutboxStore.EnqueueDeferredAsync"/>. ZeroAlloc.Outbox.EfCore's store
/// implements that by adding the row to its scoped <c>DbContext</c> without saving it, and only
/// the EF Core saga store on that same context saves it. Any other saga store drops the row with
/// the scope, so every saga command would be lost without an error.
/// </para>
/// </remarks>
internal sealed class SagaOutboxStartupCheck : IHostedLifecycleService
{
    // ZeroAlloc.Saga.Outbox references neither ZeroAlloc.Outbox.EfCore nor ZeroAlloc.Saga.EfCore,
    // and must not pull EF Core into applications that use another store, so the check
    // recognises their types by name. SagaOutboxStartupCheckTests pins each name to the real type.
    internal const string EfCoreOutboxStoreTypeName = "ZeroAlloc.Outbox.EfCore.EfCoreOutboxStore`1";
    internal const string EfCoreSagaStoreOptionsTypeName = "ZeroAlloc.Saga.EfCore.EfCoreSagaStoreOptions";
    internal const string DbContextTypeName = "Microsoft.EntityFrameworkCore.DbContext";

    internal const string SupportedSetups =
        "Supported setups:\n" +
        "  EF Core: services.AddOutbox(o => ...).WithEfCore<AppDbContext>();\n" +
        "           services.AddSaga().WithEfCoreStore<AppDbContext>()...WithOutbox();\n" +
        "  Redis:   services.AddOutbox(o => ...);\n" +
        "           services.AddSaga().WithRedisStore()...WithOutbox().WithRedisOutbox();\n" +
        "  ORM:     services.AddOutbox(o => ...).WithOrm();\n" +
        "           services.AddSaga().WithOrmStore()...WithOutbox();\n" +
        "See https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md#supported-pairings";

    internal const string SupportedPairings =
        "Supported pairings of saga store and outbox store:\n" +
        "  WithEfCoreStore<TContext>() and AddOutbox().WithEfCore<TContext>(), on the same TContext: atomic.\n" +
        "  WithRedisStore() and WithOutbox().WithRedisOutbox(): atomic.\n" +
        "  WithOrmStore() and AddOutbox().WithOrm(): at-least-once.\n" +
        "  Any saga store and an outbox store that writes each row itself, such as AddOutbox().WithOrm(): " +
        "at-least-once.\n" +
        "AddOutbox().WithEfCore<TContext>() only stages its rows, so it works with WithEfCoreStore<TContext>() " +
        "alone.\n" +
        "See https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md#supported-pairings";

    internal static string UnsupportedPairingMessage(string sagaStore, string reason, string outboxStore, string context) =>
        $"ZeroAlloc.Saga.Outbox.WithOutbox(): {sagaStore} cannot be paired with the outbox store {outboxStore}. " +
        $"That outbox store only adds each saga command to the scoped {context} and leaves saving it to the " +
        $"saga store, and {reason}, so every saga command would be lost. Configure the saga store with " +
        $"WithEfCoreStore<{context}>(), or use an outbox store that writes each row itself.\n" +
        SupportedPairings;

    internal const string NoSagaRegisteredMessage =
        "ZeroAlloc.Saga.Outbox.WithOutbox(): no saga is registered, so there is no saga command to dispatch. " +
        "Register your sagas on the same service collection with their generator-emitted With{Saga}(), " +
        "before or after WithOutbox(). A saga assembly built with a Saga generator older than this " +
        "ZeroAlloc.Saga.Outbox registers no saga command source; rebuild it.";

    internal static string MissingSerialisationMessage(IReadOnlyList<string> assemblies) =>
        "ZeroAlloc.Saga.Outbox.WithOutbox(): the saga commands of " +
        string.Join(", ", assemblies.Select(a => $"'{a}'")) +
        " cannot be dispatched from the outbox. The outbox stores serialized commands, and the Saga " +
        "generator emits the SagaCommandRegistry that deserializes them only into an assembly that " +
        "references ZeroAlloc.Serialisation. Add a ZeroAlloc.Serialisation package reference to the " +
        "project that declares those [Saga] classes.";

    internal const string MissingWorkerMessage =
        "ZeroAlloc.Saga.Outbox.WithOutbox(): no OutboxWorkerService is registered. ZeroAlloc.Outbox's " +
        "worker dispatches saga commands, and WithOutbox() does not register it: call " +
        "services.AddOutbox() once, before or after AddSaga().\n" + SupportedSetups;

    internal const string MissingStoreMessage =
        "ZeroAlloc.Saga.Outbox.WithOutbox(): no IOutboxStore is registered. Register one with " +
        "AddOutbox().WithEfCore<TContext>() when the saga store is WithEfCoreStore<TContext>(), with " +
        "WithRedisOutbox() when it is WithRedisStore(), or with AddOutbox().WithOrm().\n" +
        SupportedSetups;

    internal static string MissingSerializerMessage(IReadOnlyList<Type> commandTypes) =>
        "ZeroAlloc.Saga.Outbox.WithOutbox(): no ISerializer<T> is registered for the saga command type " +
        string.Join(", ", commandTypes.Select(t => $"'{t.FullName}'")) +
        ". The outbox serializes every step and compensation command, so each needs one. Apply " +
        "[ZeroAllocSerializable] to the command type and call the Add{Type}Serializer() that " +
        "ZeroAlloc.Serialisation generates for it, or register an ISerializer<T> yourself. See " +
        "https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md#serializers-for-step-commands";

    private readonly IServiceProvider _services;
    private readonly SagaOutboxRegistration _registration;

    public SagaOutboxStartupCheck(IServiceProvider services, SagaOutboxRegistration registration)
    {
        _services = services;
        _registration = registration;
    }

    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (_registration.SourceCount == 0)
            throw new InvalidOperationException(NoSagaRegisteredMessage);

        if (_registration.AssembliesWithoutSerialisation.Count > 0)
            throw new InvalidOperationException(MissingSerialisationMessage(_registration.AssembliesWithoutSerialisation));

        if (!HasOutboxWorker())
            throw new InvalidOperationException(MissingWorkerMessage);

        var scope = _services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            if (scope.ServiceProvider.GetService<IOutboxStore>() is not { } store)
                throw new InvalidOperationException(MissingStoreMessage);

            ThrowOnUnsupportedPairing(scope.ServiceProvider, store);
            ThrowOnConflictingDispatchers(scope.ServiceProvider);
            ThrowOnMissingSerializers(scope.ServiceProvider);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // The host has already built every IHostedService singleton by the time it calls
    // StartingAsync, so this returns those instances rather than creating new ones. Checking
    // instances also finds a worker registered through a factory, which has no ImplementationType.
    private bool HasOutboxWorker()
    {
        foreach (var service in _services.GetServices<IHostedService>())
        {
            if (service is OutboxWorkerService)
                return true;
        }

        return false;
    }

    // Throws when the saga store cannot commit what the EF Core outbox store stages. See the
    // remarks on this class.
    private void ThrowOnUnsupportedPairing(IServiceProvider scoped, IOutboxStore store)
    {
        // A backend's own unit of work, such as WithRedisOutbox()'s, decides what it commits and
        // checks its own pairing.
        if (scoped.GetService<ISagaUnitOfWork>() is not OutboxStoreSagaUnitOfWork)
            return;

        var storeType = store.GetType();
        if (!storeType.IsGenericType
            || !string.Equals(storeType.GetGenericTypeDefinition().FullName, EfCoreOutboxStoreTypeName, StringComparison.Ordinal))
        {
            // Every other store writes the row itself: at-least-once with any saga store.
            return;
        }

        var outboxContextType = storeType.GetGenericArguments()[0];

        // Each durable store points SagaRetryOptions at its own options type, so that type names
        // the store however its ISagaStore registrations are decorated.
        var sagaOptionsType = _services.GetRequiredService<SagaRetryOptions>().GetType();
        string sagaStore;
        string reason;
        if (string.Equals(sagaOptionsType.FullName, EfCoreSagaStoreOptionsTypeName, StringComparison.Ordinal))
        {
            // WithEfCoreStore<TContext>() aliases DbContext to TContext for the saga store. The
            // pairing holds when that alias is the very instance the outbox store stages into.
            var sagaContext = FindDbContextType(outboxContextType) is { } dbContextType
                ? scoped.GetService(dbContextType)
                : null;
            if (sagaContext is not null && ReferenceEquals(sagaContext, scoped.GetService(outboxContextType)))
                return;

            var sagaContextName = sagaContext is null ? "TContext" : FriendlyName(sagaContext.GetType());
            sagaStore = $"the saga store WithEfCoreStore<{sagaContextName}>()";
            reason = $"that saga store saves {sagaContextName} instead";
        }
        else
        {
            sagaStore = DescribeSagaStore(sagaOptionsType);
            reason = "that saga store never saves a DbContext";
        }

        throw new InvalidOperationException(UnsupportedPairingMessage(
            sagaStore, reason, FriendlyName(storeType), FriendlyName(outboxContextType)));
    }

    private static string DescribeSagaStore(Type sagaOptionsType)
    {
        if (sagaOptionsType == typeof(SagaRetryOptions))
            return "the InMemory saga store";

        // RedisSagaStoreOptions is configured by WithRedisStore(), OrmSagaStoreOptions by
        // WithOrmStore(), and so on.
        const string Suffix = "SagaStoreOptions";
        var name = sagaOptionsType.Name;
        return name.EndsWith(Suffix, StringComparison.Ordinal) && name.Length > Suffix.Length
            ? $"the saga store With{name[..^Suffix.Length]}Store()"
            : $"the saga store configured by {name}";
    }

    private static Type? FindDbContextType(Type context)
    {
        for (var type = context; type is not null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, DbContextTypeName, StringComparison.Ordinal))
                return type;
        }

        return null;
    }

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        var arguments = string.Join(", ", type.GetGenericArguments().Select(FriendlyName));
        return $"{(tick < 0 ? name : name[..tick])}<{arguments}>";
    }

    // Lists every command type without a serializer in one message, so the application can fix
    // them all at once. Each source probes its own types with closed-generic lookups: no
    // MakeGenericType, so the check is AOT-safe.
    private void ThrowOnMissingSerializers(IServiceProvider scoped)
    {
        List<Type>? missing = null;
        foreach (var source in _registration.SerializingSources)
        {
            var types = source.GetCommandTypesWithoutSerializer(scoped);
            if (types.Count > 0)
                (missing ??= []).AddRange(types);
        }

        if (missing is not null)
            throw new InvalidOperationException(MissingSerializerMessage(missing));
    }

    // Counts dispatchers per type name. A decorator that replaces a registration in place, as
    // ZeroAlloc.Outbox.Telemetry's WithTelemetry() does, keeps the count at one.
    private void ThrowOnConflictingDispatchers(IServiceProvider scoped)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dispatcher in scoped.GetServices<IOutboxTypeDispatcher>())
        {
            counts.TryGetValue(dispatcher.TypeName, out var count);
            counts[dispatcher.TypeName] = count + 1;
        }

        List<string>? conflicts = null;
        foreach (var typeName in _registration.TypeNames)
        {
            if (counts.TryGetValue(typeName, out var count) && count > 1)
                (conflicts ??= []).Add(typeName);
        }

        if (conflicts is null)
            return;

        throw new InvalidOperationException(
            "ZeroAlloc.Saga.Outbox.WithOutbox(): another IOutboxTypeDispatcher is registered for the saga " +
            $"command type {string.Join(", ", conflicts.Select(n => $"'{n}'"))}. ZeroAlloc.Outbox's worker " +
            "keeps one dispatcher per type name, so one of the two would never run. Remove the other " +
            "registration, or give its message type a different name.");
    }
}
