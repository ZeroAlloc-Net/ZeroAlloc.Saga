using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Saga.EfCore;

namespace ZeroAlloc.Saga.MultiAssembly.Tests;

/// <summary>
/// One DbContext with the saga and the outbox schema, so a saga state save and its outbox row
/// commit together. Same shape as the Outbox tests' OutboxE2EDbContext.
/// </summary>
public sealed class MultiAssemblyDbContext : DbContext
{
    public MultiAssemblyDbContext(DbContextOptions<MultiAssemblyDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddSagas();
        modelBuilder.AddOutboxMessages();

        // SQLite cannot compare DateTimeOffset values, which the EF outbox store's claim queries do.
        var dto = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        var nullableDto = new ValueConverter<DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : null,
            v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

        var outbox = modelBuilder.Entity<OutboxMessageEntity>();
        outbox.Property(m => m.CreatedAt).HasConversion(dto);
        outbox.Property(m => m.NextRetryAt).HasConversion(dto);
        outbox.Property(m => m.ProcessedAt).HasConversion(nullableDto);
        outbox.Property(m => m.LockedUntil).HasConversion(nullableDto);
    }
}

/// <summary>An in-memory SQLite database that lives as long as this fixture.</summary>
public sealed class SqliteFixture : IAsyncDisposable
{
    public SqliteFixture()
    {
        Connection = new SqliteConnection("DataSource=:memory:");
        Connection.Open();
    }

    public SqliteConnection Connection { get; }

    public async Task EnsureCreatedAsync()
    {
        var options = new DbContextOptionsBuilder<MultiAssemblyDbContext>().UseSqlite(Connection).Options;
        var ctx = new MultiAssemblyDbContext(options);
        await using (ctx.ConfigureAwait(false))
        {
            await ctx.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>
/// Counts the rows the outbox worker finished with, either dispatched or dead-lettered, so a test
/// can wait for every row and then look at how each one ended.
/// </summary>
public sealed class OutboxOutcomeCounter : IOutboxDashboardEventPublisher
{
    private int _dispatched;
    private int _deadLettered;
    private string? _lastDeadLetterError;

    public int Dispatched => Volatile.Read(ref _dispatched);

    public int DeadLettered => Volatile.Read(ref _deadLettered);

    public string? LastDeadLetterError => Volatile.Read(ref _lastDeadLetterError);

    public int Finished => Dispatched + DeadLettered;

    public ValueTask PublishAsync(OutboxDashboardEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case MessageDispatchedEvent:
                Interlocked.Increment(ref _dispatched);
                break;
            case MessageDeadLetteredEvent dead:
                Volatile.Write(ref _lastDeadLetterError, dead.Error);
                Interlocked.Increment(ref _deadLettered);
                break;
        }

        return default;
    }

    public OutboxDashboardSubscription Subscribe()
        => throw new NotSupportedException("OutboxOutcomeCounter only counts published events.");
}
