using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using ZeroAlloc.Saga.Orm;
using ZeroAlloc.Saga.Outbox.Orm.Tests.Fixtures;

namespace ZeroAlloc.Saga.Outbox.Orm.Tests;

/// <summary>
/// The saga and the outbox schema in one database, as <c>WithOrmOutbox()</c> requires, #226.
/// Both sources number their migrations from 1; ZeroAlloc.ORM 2.2 keeps their history apart by
/// source name. Also covers the upgrade path docs/outbox.md gives adopters who combined the two
/// sources with a version offset before that.
/// </summary>
public sealed class MigrationTests : IAsyncLifetime
{
    // Must match docs/outbox.md: the fixed name ZeroAlloc.Outbox records since 4.2.0, #229.
    private const string OutboxSourceName = "ZeroAlloc.Outbox.Orm";

    // What ZeroAlloc.Outbox 4.1 and earlier recorded on ZeroAlloc.ORM 2.2: the ORM's default name.
    private const string DefaultOutboxSourceName = "ZeroAlloc.Outbox.Orm.OutboxOrmMigrations+Source";

    // The one-line move docs/outbox.md gives for that, from ZeroAlloc.Outbox's store adapter guide.
    private const string MoveDefaultOutboxNameSql = """
        UPDATE __zaorm_migrations
        SET source = 'ZeroAlloc.Outbox.Orm'
        WHERE source = 'ZeroAlloc.Outbox.Orm.OutboxOrmMigrations+Source';
        """;

    // The SQLite reassignment docs/outbox.md gives, verbatim.
    private const string ReassignOffsetHistorySql = """
        BEGIN;
        ALTER TABLE __zaorm_migrations RENAME TO __zaorm_migrations_old;
        CREATE TABLE __zaorm_migrations (source TEXT NOT NULL, version INTEGER NOT NULL,
          name TEXT NOT NULL, applied_at TEXT NOT NULL, PRIMARY KEY (source, version));
        INSERT INTO __zaorm_migrations (source, version, name, applied_at)
          SELECT CASE WHEN version >= 1000 THEN 'ZeroAlloc.Outbox.Orm'
                      ELSE 'ZeroAlloc.Saga.Orm' END,
                 CASE WHEN version >= 1000 THEN version - 1000 ELSE version END,
                 name, applied_at
          FROM __zaorm_migrations_old;
        DROP TABLE __zaorm_migrations_old;
        COMMIT;
        """;

    private readonly SqliteFixture _fx = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _fx.DisposeAsync().AsTask();

    private static readonly string[] ScopedHistory =
    [
        "ZeroAlloc.Outbox.Orm/1/create_outbox_messages",
        "ZeroAlloc.Outbox.Orm/2/add_outbox_lease",
        "ZeroAlloc.Saga.Orm/1/create_saga_instance",
    ];

    [Fact]
    public void The_Documented_Outbox_Source_Name_Is_The_One_The_Runner_Records()
    {
        // If ZeroAlloc.Outbox ever changes it, this fails and docs/outbox.md needs the new one.
        Assert.Equal(OutboxSourceName, OutboxOrmMigrations.Sqlite.Name);
        Assert.Equal("ZeroAlloc.Saga.Orm", SagaOrmMigrations.Sqlite.Name);
    }

    [Fact]
    public async Task Two_Runners_Apply_Both_Schemas_Numbered_From_1_To_One_Database()
    {
        await _fx.MigrateAsync();

        Assert.Equal(0, await _fx.CountSagasAsync());
        Assert.Equal(0, await _fx.CountOutboxRowsAsync());
        Assert.Equal(ScopedHistory, await _fx.HistoryAsync());

        // A second run finds everything applied, per source.
        Assert.Empty(await RunAsync(SagaOrmMigrations.Sqlite));
        Assert.Empty(await RunAsync(OutboxOrmMigrations.Sqlite));
    }

    [Fact]
    public async Task Two_Sources_In_One_Runner_Call_Order_Does_Not_Matter()
    {
        // The outbox first, then the saga: neither sees the other's version 1.
        Assert.Equal(2, (await RunAsync(OutboxOrmMigrations.Sqlite)).Count);
        Assert.Single(await RunAsync(SagaOrmMigrations.Sqlite));

        Assert.Equal(ScopedHistory, await _fx.HistoryAsync());
    }

    [Fact]
    public async Task An_Offset_Adopter_Who_Keeps_Running_The_Combined_Source_Applies_Nothing_Again()
    {
        await SeedOffsetDatabaseAsync();

        var applied = await RunAsync(new AdopterCombinedMigrations());

        Assert.Empty(applied);
        Assert.Equal(
            [
                "Shop.Data.AdopterCombinedMigrations/1/create_saga_instance",
                "Shop.Data.AdopterCombinedMigrations/1001/create_outbox_messages",
                "Shop.Data.AdopterCombinedMigrations/1002/add_outbox_lease",
            ],
            await _fx.HistoryAsync());
    }

    [Fact]
    public async Task An_Offset_Adopter_Who_Reassigns_The_Rows_Can_Switch_To_Two_Runners()
    {
        await SeedOffsetDatabaseAsync();

        await _fx.ExecuteAsync(ReassignOffsetHistorySql);

        Assert.Empty(await RunAsync(SagaOrmMigrations.Sqlite));
        Assert.Empty(await RunAsync(OutboxOrmMigrations.Sqlite));
        Assert.Equal(ScopedHistory, await _fx.HistoryAsync());
    }

    [Fact]
    public async Task An_Offset_Adopter_Who_Switches_Without_Reassigning_Is_Refused_And_Nothing_Changes()
    {
        // The saga source would claim a table that also holds the outbox's offset rows. The ORM
        // refuses and rolls back, rather than letting the outbox apply its schema again.
        await SeedOffsetDatabaseAsync();

        await Assert.ThrowsAsync<ZeroAllocOrmMigrationConflictException>(() => RunAsync(SagaOrmMigrations.Sqlite));

        Assert.Equal(1, await _fx.ScalarAsync(
            "SELECT COUNT(*) = 0 FROM pragma_table_info('__zaorm_migrations') WHERE name = 'source'"));
    }

    [Fact]
    public async Task A_Saga_Only_Database_From_Before_Scoping_Upgrades_On_The_Saga_Run_Then_Takes_The_Outbox()
    {
        // ZeroAlloc.ORM 2.1 wrote the history table for the saga schema alone.
        await _fx.ExecuteAsync(
            "CREATE TABLE __zaorm_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL)");
        var saga = Assert.Single(SagaOrmMigrations.Sqlite.GetMigrations());
        await _fx.ExecuteAsync(saga.Sql);
        await _fx.ExecuteAsync(
            "INSERT INTO __zaorm_migrations (version, name, applied_at) VALUES (1, 'create_saga_instance', '2026-09-30T00:00:00.0000000Z')");

        Assert.Empty(await RunAsync(SagaOrmMigrations.Sqlite));
        Assert.Equal(2, (await RunAsync(OutboxOrmMigrations.Sqlite)).Count);

        Assert.Equal(ScopedHistory, await _fx.HistoryAsync());
    }

    [Fact]
    public async Task A_Database_That_Recorded_The_Default_Outbox_Name_Applies_Nothing_Again_After_The_Documented_Update()
    {
        // Migrated with ZeroAlloc.Outbox 4.1 on ZeroAlloc.ORM 2.2: the outbox rows sit under the
        // ORM's default name. Simulated by recording the fixed-name run under the default.
        await _fx.MigrateAsync();
        await _fx.ExecuteAsync(
            $"UPDATE __zaorm_migrations SET source = '{DefaultOutboxSourceName}' WHERE source = '{OutboxSourceName}'");

        await _fx.ExecuteAsync(MoveDefaultOutboxNameSql);

        Assert.Empty(await RunAsync(OutboxOrmMigrations.Sqlite));
        Assert.Empty(await RunAsync(SagaOrmMigrations.Sqlite));
        Assert.Equal(ScopedHistory, await _fx.HistoryAsync());
    }

    private async Task<IReadOnlyList<Migration>> RunAsync(IMigrationSource source)
    {
        var connection = _fx.Connection();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            return await new MigrationRunner(connection, source, new SqliteMigrationDialect())
                .RunAsync(default).ConfigureAwait(false);
        }
    }

    // A database migrated under ZeroAlloc.ORM 2.1 by the offset source the Saga docs used to
    // describe: the pre-scoping history table, keyed by version alone, and both schemas.
    private async Task SeedOffsetDatabaseAsync()
    {
        await _fx.ExecuteAsync(
            "CREATE TABLE __zaorm_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL)");
        foreach (var m in new AdopterCombinedMigrations().GetMigrations())
        {
            await _fx.ExecuteAsync(m.Sql);
            await _fx.ExecuteAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"INSERT INTO __zaorm_migrations (version, name, applied_at) VALUES ({m.Version}, '{m.Name}', '2026-09-30T00:00:00.0000000Z')"));
        }
    }

    // What an adopter wrote from the old docs: the saga source as is, the outbox source's
    // versions moved up by 1000. Its name is the ORM default for this type.
    private sealed class AdopterCombinedMigrations : IMigrationSource
    {
        public string Name => "Shop.Data.AdopterCombinedMigrations";

        public IReadOnlyList<Migration> GetMigrations()
        {
            var all = new List<Migration>(SagaOrmMigrations.Sqlite.GetMigrations());
            foreach (var m in OutboxOrmMigrations.Sqlite.GetMigrations())
                all.Add(m with { Version = m.Version + 1000 });
            return all;
        }
    }
}
