using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ZeroAlloc.Saga.Outbox.Tests.Fixtures;

/// <summary>
/// Per-test SQLite in-memory fixture. Owns a single <see cref="SqliteConnection"/>
/// kept open for the whole test so the in-memory database survives across
/// <see cref="DbContext"/> instances. Adapted from
/// <c>ZeroAlloc.Saga.EfCore.Tests.Fixtures.SqliteFixture</c>; targets the
/// <see cref="OutboxE2EDbContext"/> which carries both the saga and outbox schemas.
/// </summary>
public sealed class SqliteFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public SqliteConnection Connection => _connection;

    public OutboxE2EDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<OutboxE2EDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new OutboxE2EDbContext(options);
    }

    /// <summary>
    /// Materialises the combined saga + outbox schema. Call once after the
    /// fixture is built (typically from a test's prologue).
    /// </summary>
    public async Task EnsureCreatedAsync()
    {
        var ctx = CreateContext();
        await using (ctx.ConfigureAwait(false))
        {
            await ctx.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
