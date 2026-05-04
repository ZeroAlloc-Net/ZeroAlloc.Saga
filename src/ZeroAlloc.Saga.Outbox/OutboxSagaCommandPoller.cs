using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Outbox;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// Hosted service that polls <see cref="IOutboxStore"/> for pending saga commands and
/// dispatches each via <see cref="SagaCommandRegistryDispatcher"/>. On success, marks the
/// entry succeeded; on failure, either schedules a retry or moves the entry to dead-letter
/// once <see cref="OutboxSagaPollerOptions.MaxRetries"/> is exhausted.
/// </summary>
public sealed class OutboxSagaCommandPoller : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SagaCommandRegistryDispatcher _dispatch;
    private readonly ILogger<OutboxSagaCommandPoller> _log;
    private readonly OutboxSagaPollerOptions _options;

    public OutboxSagaCommandPoller(
        IServiceScopeFactory scopeFactory,
        SagaCommandRegistryDispatcher dispatch,
        ILogger<OutboxSagaCommandPoller> log,
        OutboxSagaPollerOptions? options = null)
    {
        _scopeFactory = scopeFactory;
        _dispatch = dispatch;
        _log = log;
        _options = options ?? new OutboxSagaPollerOptions();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
#pragma warning disable CA1031 // Background loop must not crash on per-cycle exceptions.
            catch (Exception ex)
            {
                _log.LogError(ex, "OutboxSagaCommandPoller cycle failed");
            }
#pragma warning restore CA1031

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs a single poll cycle: fetch pending entries, dispatch each, mark succeeded /
    /// scheduled for retry / dead-lettered. Exposed for unit tests.
    /// </summary>
    public async ValueTask PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var pending = await store.FetchPendingAsync(_options.BatchSize, ct).ConfigureAwait(false);
        foreach (var entry in pending)
        {
            try
            {
                await _dispatch(entry.TypeName, entry.Payload, scope.ServiceProvider, ct).ConfigureAwait(false);
                await store.MarkSucceededAsync(entry.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Per-entry failure isolation: one bad message must not stop the cycle.
            catch (Exception ex)
            {
                if (entry.RetryCount + 1 >= _options.MaxRetries)
                {
                    await store.DeadLetterAsync(entry.Id, ex.ToString(), ct).ConfigureAwait(false);
                }
                else
                {
                    await store.MarkFailedAsync(
                        entry.Id,
                        entry.RetryCount + 1,
                        DateTimeOffset.UtcNow.Add(_options.RetryDelay),
                        ct).ConfigureAwait(false);
                }
            }
#pragma warning restore CA1031
        }
    }
}
