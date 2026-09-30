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

    /// <summary>
    /// Applies the saga schema and, unless told otherwise, the outbox schema, each through its
    /// own runner as docs/outbox.md shows. Both number their migrations from 1; the history table
    /// keeps them apart by source name.
    /// </summary>
    public async Task MigrateAsync(bool withOutbox = true, CancellationToken ct = default)
    {
        var connection = Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var dialect = new SqliteMigrationDialect();
            await new MigrationRunner(connection, SagaOrmMigrations.Sqlite, dialect).RunAsync(ct).ConfigureAwait(false);
            if (withOutbox)
                await new MigrationRunner(connection, OutboxOrmMigrations.Sqlite, dialect).RunAsync(ct).ConfigureAwait(false);
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

    /// <summary>The history table's rows as <c>source/version/name</c>, sorted.</summary>
    public async Task<string[]> HistoryAsync()
    {
        var rows = new List<string>();
        var connection = Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            var cmd = connection.CreateCommand();
            await using (cmd.ConfigureAwait(false))
            {
                cmd.CommandText = "SELECT source, version, name FROM __zaorm_migrations";
                await using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    rows.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{reader.GetString(0)}/{reader.GetInt64(1)}/{reader.GetString(2)}"));
                }
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

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

    public async Task<long> ScalarAsync(string sql)
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
