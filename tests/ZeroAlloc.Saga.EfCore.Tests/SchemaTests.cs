using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ZeroAlloc.Saga.EfCore.Tests.Fixtures;

namespace ZeroAlloc.Saga.EfCore.Tests;

/// <summary>
/// Schema-level tests: assert the model registered by
/// <see cref="SagaModelBuilderExtensions.AddSagas(ModelBuilder)"/> declares the
/// shape promised by the Phase 2 design — composite primary key, concurrency
/// token, indexes — and that EF Core can materialise the schema against
/// SQLite via <c>EnsureCreated</c>.
/// </summary>
public sealed class SchemaTests
{
    [Fact]
    public async Task Schema_AddSagasInModelBuilder_RegistersEntity()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();

        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var entityType = ctx.Model.FindEntityType(typeof(SagaInstanceEntity));
            Assert.NotNull(entityType);
            Assert.Equal("SagaInstance", entityType!.GetTableName());
        }
    }

    [Fact]
    public async Task Schema_PrimaryKey_IsCompositeOf_SagaTypeAndCorrelationKey()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();

        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var entityType = ctx.Model.FindEntityType(typeof(SagaInstanceEntity))!;
            var pk = entityType.FindPrimaryKey()!;
            var names = pk.Properties.Select(p => p.Name).ToArray();
            Assert.Equal(new[] { nameof(SagaInstanceEntity.SagaType), nameof(SagaInstanceEntity.CorrelationKey) }, names);
        }
    }

    [Fact]
    public async Task Schema_RowVersion_MappedAsConcurrencyToken()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();

        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            var entityType = ctx.Model.FindEntityType(typeof(SagaInstanceEntity))!;
            var rowVersion = entityType.FindProperty(nameof(SagaInstanceEntity.RowVersion))!;
            Assert.True(rowVersion.IsConcurrencyToken,
                "RowVersion must be marked as a concurrency token so EF includes it in the WHERE clause on UPDATE/DELETE.");
        }
    }

    [Fact]
    public async Task Migration_EnsureCreated_BuildsValidSchema()
    {
        await using var fx = new SqliteFixture();
        await fx.EnsureCreatedAsync();

        // Insert + read back through the schema produced by EnsureCreated.
        var ctx = fx.CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            ctx.Set<SagaInstanceEntity>().Add(new SagaInstanceEntity
            {
                SagaType = "Test.Saga",
                CorrelationKey = "key-1",
                State = new byte[] { 1, 2, 3 },
                CurrentFsmState = "Step1",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                RowVersion = Guid.NewGuid().ToByteArray(),
            });
            await ctx.SaveChangesAsync();

            var rows = await ctx.Set<SagaInstanceEntity>().AsNoTracking().ToListAsync();
            Assert.Single(rows);
            Assert.Equal("Test.Saga", rows[0].SagaType);
        }
    }
}
