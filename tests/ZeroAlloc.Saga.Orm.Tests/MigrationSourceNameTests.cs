using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Saga.Orm.Tests;

/// <summary>
/// The saga schema's migration source name, #226. ZeroAlloc.ORM records it with every migration
/// the source applies and finds the applied versions by it, so a changed name re-applies the
/// schema. It is a fixed string, not the default taken from the source's type name.
/// </summary>
public sealed class MigrationSourceNameTests
{
    public static TheoryData<string, IMigrationSource> Sources => new()
    {
        { "Sqlite", SagaOrmMigrations.Sqlite },
        { "Postgres", SagaOrmMigrations.Postgres },
        { "SqlServer", SagaOrmMigrations.SqlServer },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public void Every_Dialect_Uses_The_Fixed_Name(string dialect, IMigrationSource source)
    {
        Assert.False(string.IsNullOrEmpty(dialect));

        // Pinned as a literal: changing it would make every existing database apply the saga
        // schema again.
        Assert.Equal("ZeroAlloc.Saga.Orm", source.Name);
    }
}
