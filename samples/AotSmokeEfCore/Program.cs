using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZeroAlloc.Mediator;
using ZeroAlloc.Saga;
using ZeroAlloc.Saga.EfCore;

namespace AotSmokeEfCore;

/// <summary>
/// AOT-compatibility smoke test for the EfCore backend.
///
/// What this verifies:
///   1. The runtime library (ZeroAlloc.Saga.EfCore) + the saga generator
///      output build clean under IsAotCompatible=true + EnableTrimAnalyzer
///      (the analyzer set lights up the same warnings PublishAot=true does
///      at link-time).
///   2. The EfCore backend executes the full saga round-trip end-to-end
///      against a real SQLite database — OrderPlaced → StockReserved →
///      PaymentCharged, with each save persisting to the SagaInstance
///      table and the RowVersion concurrency token rotating between
///      saves.
///   3. The terminal Completed state removes the saga row.
///
/// Why this isn't a true PublishAot=true binary today: EF Core 9.0's
/// NativeAOT support is experimental and requires both a compiled model
/// (which this sample's sibling project AotSmokeEfCore.Model demonstrates)
/// AND precompiled queries. The runtime library uses dynamic LINQ
/// expressions inside its store implementation that the
/// `--precompile-queries` flow doesn't yet cover. We track upstream EF
/// Core's progress and will flip PublishAot=true on this csproj when it
/// matures (post-10.0).
/// </summary>
internal static class Program
{
    /// <summary>
    /// Resolves the saga's generated <see cref="INotificationHandler{T}"/>
    /// from a fresh DI scope and invokes it. Every event runs in its own
    /// scope so each handler resolves a fresh DbContext — mirroring the
    /// per-message scope a real host (HTTP / message-bus consumer) would
    /// allocate.
    /// </summary>
    private static async Task PublishAsync<T>(IServiceProvider sp, T evt) where T : INotification
    {
        using var scope = sp.CreateScope();
        foreach (var h in scope.ServiceProvider.GetServices<INotificationHandler<T>>())
            await h.Handle(evt, default).ConfigureAwait(false);
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("AotSmokeEfCore: starting saga end-to-end with EfCore + SQLite in-memory");

        var counters = new CommandCounters();
        CommandCounters.Current = counters;

        // Hold a single SQLite connection open for the whole run so the
        // in-memory DB survives across DbContexts.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        // Real IMediator wired by the Mediator source generator.
        services.AddMediator();

        services.AddDbContext<TestDbContext>(opts => opts.UseSqlite(connection),
            ServiceLifetime.Scoped);

        // WithEfCoreStore<TContext>() MUST come BEFORE AddXxxSaga() — order
        // is enforced by an InvalidOperationException guard in the builder.
        services.AddSaga()
            .WithEfCoreStore<TestDbContext>(opts =>
            {
                opts.MaxRetryAttempts = 3;
                opts.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                opts.UseExponentialBackoff = false;
            })
            .AddOrderFulfillmentSaga();
        var sp = services.BuildServiceProvider();

        // Materialise the SagaInstance schema before publishing any events.
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await ctx.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        var orderId = new OrderId(42);

        // Step 1: Publish OrderPlaced — saga creates a row in SagaInstance.
        await PublishAsync(sp, new OrderPlaced(orderId, 100m));
        if (counters.Reserve != 1) return Fail($"Expected Reserve=1 after OrderPlaced, got {counters.Reserve}");

        byte[]? rowVersionAfterStep1;
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            var rows = await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync().ConfigureAwait(false);
            if (rows.Count != 1) return Fail($"Expected 1 saga row after OrderPlaced, got {rows.Count}");
            rowVersionAfterStep1 = rows[0].RowVersion;
        }

        // Step 2: Publish StockReserved — saga advances; RowVersion rotates.
        await PublishAsync(sp, new StockReserved(orderId));
        if (counters.Charge != 1) return Fail($"Expected Charge=1 after StockReserved, got {counters.Charge}");

        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            var rows = await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync().ConfigureAwait(false);
            if (rows.Count != 1) return Fail($"Expected 1 saga row after StockReserved, got {rows.Count}");
            if (rows[0].RowVersion.SequenceEqual(rowVersionAfterStep1!))
                return Fail("RowVersion should have rotated after StockReserved (OCC token unchanged)");
        }

        // Step 3: Publish PaymentCharged — saga reaches Completed; row removed.
        await PublishAsync(sp, new PaymentCharged(orderId));
        if (counters.Ship != 1) return Fail($"Expected Ship=1 after PaymentCharged, got {counters.Ship}");

        // Forward path ran exactly once; no compensation fired.
        if (counters.Cancel != 0) return Fail($"Expected Cancel=0, got {counters.Cancel}");
        if (counters.Refund != 0) return Fail($"Expected Refund=0, got {counters.Refund}");

        // Saga removed from store after Step 3 (terminal Completed state).
        using (var scope = sp.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            var rows = await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync().ConfigureAwait(false);
            if (rows.Count != 0) return Fail($"Expected 0 saga rows after Completed, got {rows.Count}");
        }

        Console.WriteLine("AotSmokeEfCore: OK — saga reached Completed, RowVersion rotated, row removed.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"AotSmokeEfCore: FAIL — {message}");
        return 1;
    }
}
