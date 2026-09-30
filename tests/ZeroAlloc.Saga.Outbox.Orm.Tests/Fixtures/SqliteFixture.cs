using System;
using System.Collections.Generic;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Saga.Orm;

namespace ZeroAlloc.Saga.Outbox.Orm.Tests.Fixtures;

/// <summary>
/// A SQLite database file holding both the saga table and the outbox table, as an application
/// using <c>WithOrmOutbox()</c> has them: in one database, so one transaction spans both.
/// </summary>
/// <remarks>
/// A file rather than an in-memory database: the outbox worker and the test's own counting
/// queries open connections of their own, and a file gives them ordinary SQLite locking.
/// Pooling is off so the file can be deleted when the fixture is disposed.
/// </remarks>
public sealed class SqliteFixture : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"saga-outbox-orm-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_path};Pooling=False";

    /// <summary>A new, unopened connection onto the fixture's database.</summary>
    public IAsyncDbConnection Connection() => new SqliteConnection(ConnectionString).AsAsync();

    /// <summary>Applies the saga schema and, unless told otherwise, the outbox schema.</summary>
    public async Task MigrateAsync(bool withOutbox = true, CancellationToken ct = default)
    {
        var connection = Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var source = withOutbox
                ? CombinedMigrations.Of(SagaOrmMigrations.Sqlite, OutboxOrmMigrations.Sqlite)
                : SagaOrmMigrations.Sqlite;
            await new MigrationRunner(connection, source, new SqliteMigrationDialect())
                .RunAsync(ct).ConfigureAwait(false);
        }
    }

    public Task<long> CountSagasAsync() => ScalarAsync("SELECT COUNT(*) FROM SagaInstance");

    public Task<long> CountOutboxRowsAsync() => ScalarAsync("SELECT COUNT(*) FROM OutboxMessages");

    /// <summary>The type name of every outbox row, sorted.</summary>
    public async Task<string[]> OutboxTypeNamesAsync()
    {
        var names = new List<string>();
        var connection = Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT TypeName FROM OutboxMessages";
                // IAsyncDbDataReader is both disposable and enumerable, so ConfigureAwait on it
                // is ambiguous; this assembly waives MA0004 anyway.
                await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                    names.Add(reader.GetString(0));
            }
        }

        names.Sort(StringComparer.Ordinal);
        return [.. names];
    }

    /// <summary>
    /// Moves a saga row's version, as another writer's save would, from a connection of its own.
    /// </summary>
    public Task ChangeRowVersionBehindTheStoreAsync(string correlationKey)
        => ExecuteAsync(
            "UPDATE SagaInstance SET RowVersion = randomblob(16) WHERE CorrelationKey = '" + correlationKey + "'");

    public async Task ExecuteAsync(string sql)
    {
        var connection = Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<long> ScalarAsync(string sql)
    {
        var connection = Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = sql;
                var scalar = await cmd.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
                return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A worker connection still closing; the temp directory is cleaned eventually.
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Runs two libraries' migrations through one <see cref="MigrationRunner"/>. The runner keeps one
/// history table per database, keyed by version, and both the saga and the outbox schema number
/// their migrations from 1. So the second source's versions are moved past the first's.
/// </summary>
public sealed class CombinedMigrations : IMigrationSource
{
    private const int SecondSourceOffset = 1000;
    private readonly IReadOnlyList<Migration> _migrations;

    private CombinedMigrations(IReadOnlyList<Migration> migrations) => _migrations = migrations;

    public static IMigrationSource Of(IMigrationSource first, IMigrationSource second)
    {
        var all = new List<Migration>(first.GetMigrations());
        foreach (var m in second.GetMigrations())
            all.Add(m with { Version = m.Version + SecondSourceOffset });
        return new CombinedMigrations(all);
    }

    public IReadOnlyList<Migration> GetMigrations() => _migrations;
}
