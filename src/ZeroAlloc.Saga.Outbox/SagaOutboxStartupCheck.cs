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
/// It throws when the generator-emitted registry was not found, when no
/// <see cref="OutboxWorkerService"/> is registered, when no <see cref="IOutboxStore"/> resolves,
/// or when another <see cref="IOutboxTypeDispatcher"/> claims a saga command's type name. The
/// worker keeps one dispatcher per type name, the last one registered, so the other one would
/// never run.
/// </para>
/// </remarks>
internal sealed class SagaOutboxStartupCheck : IHostedLifecycleService
{
    internal const string SupportedSetups =
        "Supported setups:\n" +
        "  EF Core: services.AddOutbox(o => ...).WithEfCore<AppDbContext>();\n" +
        "           services.AddSaga()...WithOutbox();\n" +
        "  Redis:   services.AddOutbox(o => ...);\n" +
        "           services.AddSaga()...WithRedisStore()...WithOutbox().WithRedisOutbox();\n" +
        "See https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/outbox.md";

    internal const string RegistryNotFoundMessage =
        "ZeroAlloc.Saga.Outbox.WithOutbox(): could not locate the generator-emitted " +
        "ZeroAlloc.Saga.Generated.SagaCommandRegistry. The Saga generator emits it into the assembly " +
        "that declares your [Saga] classes when that assembly references ZeroAlloc.Serialisation, " +
        "and WithOutbox() needs one built by ZeroAlloc.Saga 4.0 or later. That assembly must be " +
        "loaded when WithOutbox() runs, which it is when the same method calls its With{Saga}Saga().";

    internal const string MissingWorkerMessage =
        "ZeroAlloc.Saga.Outbox.WithOutbox(): no OutboxWorkerService is registered. ZeroAlloc.Outbox's " +
        "worker dispatches saga commands, and WithOutbox() does not register it: call " +
        "services.AddOutbox() once, before or after AddSaga().\n" + SupportedSetups;

    internal const string MissingStoreMessage =
        "ZeroAlloc.Saga.Outbox.WithOutbox(): no IOutboxStore is registered. Register one with " +
        "AddOutbox().WithEfCore<TContext>(), or with WithRedisOutbox() when the saga store is Redis.\n" +
        SupportedSetups;

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
        if (!_registration.RegistryFound)
            throw new InvalidOperationException(RegistryNotFoundMessage);

        if (!HasOutboxWorker())
            throw new InvalidOperationException(MissingWorkerMessage);

        var scope = _services.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            if (scope.ServiceProvider.GetService<IOutboxStore>() is null)
                throw new InvalidOperationException(MissingStoreMessage);

            ThrowOnConflictingDispatchers(scope.ServiceProvider);
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
