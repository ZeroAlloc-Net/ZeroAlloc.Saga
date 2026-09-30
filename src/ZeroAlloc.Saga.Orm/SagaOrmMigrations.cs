using ZeroAlloc.ORM.Migrations;

namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// The saga backend's schema, as an <see cref="IMigrationSource"/> you hand to
/// ZeroAlloc.ORM's <c>MigrationRunner</c> alongside the dialect for your
/// database.
/// </summary>
/// <remarks>
/// <para>
/// Supplied as code rather than embedded <c>.sql</c> resources because the DDL
/// is the one part of this backend that is genuinely provider-specific —
/// <c>BLOB</c> versus <c>BYTEA</c>, <c>TEXT</c> versus <c>VARCHAR</c>. The
/// resource-naming convention gives a single migration set per assembly, which
/// cannot express that; selecting the statement by dialect can.
/// </para>
/// <para>
/// Every source here is named <c>ZeroAlloc.Saga.Orm</c>. ZeroAlloc.ORM records
/// that name with each migration it applies and numbers each source's versions
/// on its own, so this schema shares one database and one history table with
/// your application's migrations and with ZeroAlloc.Outbox.Orm's, although all
/// of them start at version 1. Run each source through its own
/// <c>MigrationRunner</c>. The name is fixed, not taken from a type name, because
/// a changed name makes the runner apply the schema again.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var runner = new MigrationRunner(connection, SagaOrmMigrations.Sqlite, new SqliteMigrationDialect());
/// await runner.RunAsync(ct);
/// </code>
/// </example>
public static class SagaOrmMigrations
{
    /// <summary>Schema for SQLite.</summary>
    public static IMigrationSource Sqlite { get; } = new Source("""
        CREATE TABLE IF NOT EXISTS SagaInstance (
            SagaType        TEXT NOT NULL,
            CorrelationKey  TEXT NOT NULL,
            State           BLOB NOT NULL,
            CurrentFsmState TEXT NOT NULL,
            RowVersion      BLOB NOT NULL,
            CreatedAt       TEXT NOT NULL,
            UpdatedAt       TEXT NOT NULL,
            PRIMARY KEY (SagaType, CorrelationKey)
        );
        """);

    /// <summary>Schema for PostgreSQL.</summary>
    public static IMigrationSource Postgres { get; } = new Source("""
        CREATE TABLE IF NOT EXISTS SagaInstance (
            SagaType        VARCHAR(512) NOT NULL,
            CorrelationKey  VARCHAR(512) NOT NULL,
            State           BYTEA NOT NULL,
            CurrentFsmState VARCHAR(256) NOT NULL,
            RowVersion      BYTEA NOT NULL,
            CreatedAt       TIMESTAMPTZ NOT NULL,
            UpdatedAt       TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (SagaType, CorrelationKey)
        );
        """);

    /// <summary>Schema for Microsoft SQL Server.</summary>
    /// <remarks>
    /// The store's SQL is plain SELECT/INSERT/UPDATE/DELETE with no paging or
    /// RETURNING, so nothing beyond the DDL differs on SQL Server.
    /// <para>
    /// Column widths match the EF Core adapter so both write the same table. The
    /// key is NONCLUSTERED because NVARCHAR(512) plus NVARCHAR(256) is 1536 bytes,
    /// over SQL Server's 900-byte clustered index key limit but inside the
    /// 1700-byte nonclustered limit. Left clustered, the table still creates --
    /// SQL Server only warns -- and then fails at INSERT once a saga type and
    /// correlation key are long enough together, which is a failure that would
    /// surface in production rather than in a test with short keys.
    /// </para>
    /// </remarks>
    public static IMigrationSource SqlServer { get; } = new Source("""
        IF OBJECT_ID(N'SagaInstance', N'U') IS NULL
        CREATE TABLE SagaInstance (
            SagaType        NVARCHAR(512) NOT NULL,
            CorrelationKey  NVARCHAR(256) NOT NULL,
            State           VARBINARY(MAX) NOT NULL,
            CurrentFsmState NVARCHAR(256) NOT NULL,
            RowVersion      VARBINARY(16) NOT NULL,
            CreatedAt       DATETIMEOFFSET NOT NULL,
            UpdatedAt       DATETIMEOFFSET NOT NULL,
            CONSTRAINT PK_SagaInstance PRIMARY KEY NONCLUSTERED (SagaType, CorrelationKey)
        );
        """);

    private sealed class Source(string sql) : IMigrationSource
    {
        private readonly IReadOnlyList<Migration> _migrations =
            [new Migration(1, "create_saga_instance", sql)];

        // One name for every dialect: a database has one of them. Never change
        // it: the history table finds this schema's applied versions by it.
        public string Name => "ZeroAlloc.Saga.Orm";

        public IReadOnlyList<Migration> GetMigrations() => _migrations;
    }
}
