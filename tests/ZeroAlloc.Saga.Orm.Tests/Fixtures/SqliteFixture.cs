using System;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Saga.Orm.Tests.Fixtures;

/// <summary>
/// A SQLite database that lives as long as the fixture, with the saga schema
/// applied through the ORM's own <see cref="MigrationRunner"/>.
/// </summary>
/// <remarks>
/// Backed by a shared-cache in-memory database rather than a plain
/// <c>:memory:</c> one: the OCC tests need two independent connections looking
/// at the same rows, and a plain in-memory database is private to its
/// connection. The keep-alive connection below holds the database open, since
/// a shared-cache database is dropped once the last connection to it closes.
/// </remarks>
public sealed class SqliteFixture : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _keepAlive;

    public SqliteFixture()
    {
        _connectionString = $"Data Source=saga-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    /// <summary>Opens a new connection onto the fixture's database.</summary>
    public async Task<IAsyncDbConnection> ConnectAsync(CancellationToken ct = default)
    {
        var raw = new SqliteConnection(_connectionString);
        await raw.OpenAsync(ct).ConfigureAwait(false);
        return raw.AsAsync();
    }

    /// <summary>Applies the saga schema the same way an application would.</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var connection = await ConnectAsync(ct).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var runner = new MigrationRunner(
                connection, SagaOrmMigrations.Sqlite, new SqliteMigrationDialect());
            await runner.RunAsync(ct).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _keepAlive.Dispose();
        return ValueTask.CompletedTask;
    }
}
