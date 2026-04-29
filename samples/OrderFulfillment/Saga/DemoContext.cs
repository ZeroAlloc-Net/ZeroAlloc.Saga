using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Saga.EfCore;

namespace OrderFulfillment.Saga;

/// <summary>
/// <see cref="DbContext"/> used by the OrderFulfillment demo when launched
/// with <c>--efcore</c>. Calls
/// <see cref="SagaModelBuilderExtensions.AddSagas(ModelBuilder)"/> in
/// <see cref="OnModelCreating(ModelBuilder)"/> so the SQLite database file
/// gets the shared <c>SagaInstance</c> table.
/// </summary>
public sealed class DemoContext : DbContext
{
    public DemoContext(DbContextOptions<DemoContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddSagas();
    }
}
