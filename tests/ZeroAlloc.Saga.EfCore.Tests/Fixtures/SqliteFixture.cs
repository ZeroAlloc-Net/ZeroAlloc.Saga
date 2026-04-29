using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ZeroAlloc.Saga.EfCore.Tests.Fixtures;

/// <summary>
/// Per-test SQLite in-memory fixture. Owns a single <see cref="SqliteConnection"/>
/// kept open for the whole test so the in-memory database survives across
/// <see cref="DbContext"/> instances. Tests construct one fixture per
/// <c>InitializeAsync</c>, then dispose it.
/// </summary>
/// <remarks>
/// Each fixture instance gets a unique <c>Cache=Shared;Filename=...</c> moniker
/// so tests running in parallel do not collide. The connection persists for
/// the fixture lifetime; <see cref="CreateContext"/> can be called repeatedly
/// to spawn fresh contexts that share the same database — used by tests that
/// simulate concurrent updates and process-restart scenarios.
/// </remarks>
public sealed class SqliteFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    /// <summary>The shared connection — exposed for low-level test inspection.</summary>
    public SqliteConnection Connection => _connection;

    /// <summary>
    /// Creates a new <see cref="TestDbContext"/> bound to the fixture's
    /// in-memory database. Tests that simulate cross-context concurrency
    /// or "process restart" call this multiple times.
    /// </summary>
    public TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new TestDbContext(options);
    }

    /// <summary>
    /// Materialises the saga schema. Call once after the fixture is built
    /// (from a test's <c>InitializeAsync</c> or from the test body).
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
