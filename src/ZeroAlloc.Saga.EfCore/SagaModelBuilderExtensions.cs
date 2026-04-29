using Microsoft.EntityFrameworkCore;

namespace ZeroAlloc.Saga.EfCore;

/// <summary>
/// <see cref="ModelBuilder"/> extensions that register the saga
/// persistence schema on a user's <see cref="DbContext"/>.
/// </summary>
public static class SagaModelBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="SagaInstanceEntity"/> on the supplied
    /// <see cref="ModelBuilder"/>: composite primary key on
    /// <c>(SagaType, CorrelationKey)</c>, row-version token, and a covering
    /// index on <c>(SagaType, UpdatedAt)</c> for housekeeping queries.
    /// </summary>
    /// <remarks>
    /// Call from <c>OnModelCreating</c>:
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder mb)
    /// {
    ///     mb.AddSagas();
    /// }
    /// </code>
    /// The schema is stable across PR 2 (scaffold) and PR 3 (full impl).
    /// </remarks>
    public static ModelBuilder AddSagas(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entity = modelBuilder.Entity<SagaInstanceEntity>();
        entity.ToTable("SagaInstance");
        entity.HasKey(e => new { e.SagaType, e.CorrelationKey });
        entity.Property(e => e.SagaType).HasMaxLength(512).IsRequired();
        entity.Property(e => e.CorrelationKey).HasMaxLength(256).IsRequired();
        entity.Property(e => e.State).IsRequired();
        entity.Property(e => e.CurrentFsmState).HasMaxLength(128).IsRequired();
        // RowVersion drives optimistic concurrency. Mapped as a concurrency
        // token rather than IsRowVersion() so the column is supported uniformly
        // across providers (SQL Server's native ROWVERSION, PostgreSQL's xmin,
        // and SQLite — which has no native row-version — all work because the
        // store updates the value explicitly on every save). EF treats the
        // value as the WHERE-clause condition on UPDATE/DELETE, raising
        // DbUpdateConcurrencyException when the row was changed underneath.
        entity.Property(e => e.RowVersion).IsConcurrencyToken().IsRequired();
        entity.Property(e => e.CreatedAt).IsRequired();
        entity.Property(e => e.UpdatedAt).IsRequired();
        entity.HasIndex(e => new { e.SagaType, e.UpdatedAt });
        return modelBuilder;
    }
}
