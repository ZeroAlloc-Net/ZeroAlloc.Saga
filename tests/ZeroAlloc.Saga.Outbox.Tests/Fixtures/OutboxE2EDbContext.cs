using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ZeroAlloc.Outbox.EfCore;
using ZeroAlloc.Saga.EfCore;

namespace ZeroAlloc.Saga.Outbox.Tests.Fixtures;

/// <summary>
/// Combined <see cref="DbContext"/> that registers BOTH the saga schema (via
/// <see cref="SagaModelBuilderExtensions.AddSagas"/>) AND the outbox messages
/// table (via <see cref="OutboxDbContextExtensions.AddOutboxMessages"/>). This
/// is the linchpin of the atomic-dispatch contract: a single scoped DbContext
/// with both schemas means a single <c>SaveChangesAsync</c> commits a saga
/// state row and an outbox row in the same implicit transaction.
/// </summary>
public sealed class OutboxE2EDbContext : DbContext
{
    public OutboxE2EDbContext(DbContextOptions<OutboxE2EDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddSagas();
        modelBuilder.AddOutboxMessages();

        // SQLite can't translate DateTimeOffset comparisons natively; convert to a
        // sortable tick-based representation so OutboxStore.FetchPendingAsync queries
        // (NextRetryAt <= now) translate. Mirrors DashboardTestDbContext from
        // ZeroAlloc.Outbox.Tests.
        var dtoConverter = new ValueConverter<System.DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new System.DateTimeOffset(v, System.TimeSpan.Zero));
        var nullableDtoConverter = new ValueConverter<System.DateTimeOffset?, long?>(
            v => v.HasValue ? v.Value.UtcTicks : null,
            v => v.HasValue ? new System.DateTimeOffset(v.Value, System.TimeSpan.Zero) : null);

        var outbox = modelBuilder.Entity<OutboxMessageEntity>();
        outbox.Property(m => m.CreatedAt).HasConversion(dtoConverter);
        outbox.Property(m => m.NextRetryAt).HasConversion(dtoConverter);
        outbox.Property(m => m.ProcessedAt).HasConversion(nullableDtoConverter);
    }
}
