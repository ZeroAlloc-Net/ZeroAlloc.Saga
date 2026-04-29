using Microsoft.EntityFrameworkCore;
using ZeroAlloc.Saga.EfCore;

namespace ZeroAlloc.Saga.EfCore.Tests.Fixtures;

/// <summary>
/// Minimal <see cref="DbContext"/> used by the EF Core test suite. Calls
/// <see cref="SagaModelBuilderExtensions.AddSagas(ModelBuilder)"/> in
/// <see cref="OnModelCreating(ModelBuilder)"/> so SQLite materialises the
/// shared SagaInstance schema during <c>EnsureCreated</c>.
/// </summary>
public sealed class TestDbContext : DbContext
{
    public TestDbContext(DbContextOptions<TestDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddSagas();
    }
}
