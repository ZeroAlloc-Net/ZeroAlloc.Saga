using System.Data.Async;
using ZeroAlloc.ORM;

namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// The SQL statements the saga store needs, as ZeroAlloc.ORM partial
/// methods. The generator emits the parameter binding and materialisation at
/// compile time, so nothing here reflects at runtime.
/// </summary>
/// <remarks>
/// <para>
/// The SQL is intentionally plain ANSI: no provider-specific syntax appears in
/// any statement, so the same repository serves SQLite and PostgreSQL. Only the
/// DDL differs between them, and that lives in <see cref="SagaOrmMigrations"/>.
/// </para>
/// <para>
/// <see cref="UpdateAsync"/> and <see cref="DeleteVersionedAsync"/> carry the
/// optimistic-concurrency predicate. They match on the row version the caller
/// read, and the update rotates it in the same statement. A caller whose
/// version is stale affects zero rows, which the store turns into
/// <see cref="OrmSagaConcurrencyException"/> — there is no read-then-write
/// window for a competing writer to slip through.
/// </para>
/// <para>
/// Every write takes the store's <see cref="IAsyncDbTransaction"/>, so the ORM
/// emits <c>cmd.Transaction = tx</c> and the statement commits with whatever
/// the store's contributors write in the same transaction. Only the load runs
/// on its own.
/// </para>
/// </remarks>
internal sealed partial class SagaInstanceRepository(IAsyncDbConnection connection)
{
    /// <summary>The connection every statement runs on, and the store's transactions begin on.</summary>
    public IAsyncDbConnection Connection => connection;

    [Query("""
        SELECT State, CurrentFsmState, RowVersion
        FROM SagaInstance
        WHERE SagaType = @sagaType AND CorrelationKey = @correlationKey
        """)]
    public partial Task<SagaInstanceRow?> GetAsync(
        string sagaType, string correlationKey, CancellationToken ct);

    // The same read, inside the store's transaction: RemoveAsync checks that no
    // row appeared since a load found none, and the check belongs to the
    // transaction that commits the removal's contributed writes.
    [Query("""
        SELECT State, CurrentFsmState, RowVersion
        FROM SagaInstance
        WHERE SagaType = @sagaType AND CorrelationKey = @correlationKey
        """)]
    public partial Task<SagaInstanceRow?> GetInTransactionAsync(
        string sagaType, string correlationKey, IAsyncDbTransaction tx, CancellationToken ct);

    [Command("""
        INSERT INTO SagaInstance
            (SagaType, CorrelationKey, State, CurrentFsmState, RowVersion, CreatedAt, UpdatedAt)
        VALUES
            (@sagaType, @correlationKey, @state, @currentFsmState, @rowVersion, @createdAt, @updatedAt)
        """)]
    public partial Task<int> InsertAsync(
        string sagaType,
        string correlationKey,
        byte[] state,
        string currentFsmState,
        byte[] rowVersion,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        IAsyncDbTransaction tx,
        CancellationToken ct);

    [Command("""
        UPDATE SagaInstance
        SET State = @state,
            CurrentFsmState = @currentFsmState,
            RowVersion = @newRowVersion,
            UpdatedAt = @updatedAt
        WHERE SagaType = @sagaType
          AND CorrelationKey = @correlationKey
          AND RowVersion = @expectedRowVersion
        """)]
    public partial Task<int> UpdateAsync(
        string sagaType,
        string correlationKey,
        byte[] state,
        string currentFsmState,
        byte[] newRowVersion,
        byte[] expectedRowVersion,
        DateTimeOffset updatedAt,
        IAsyncDbTransaction tx,
        CancellationToken ct);

    [Command("""
        DELETE FROM SagaInstance
        WHERE SagaType = @sagaType AND CorrelationKey = @correlationKey
        """)]
    public partial Task<int> DeleteAsync(
        string sagaType, string correlationKey, IAsyncDbTransaction tx, CancellationToken ct);

    [Command("""
        DELETE FROM SagaInstance
        WHERE SagaType = @sagaType
          AND CorrelationKey = @correlationKey
          AND RowVersion = @expectedRowVersion
        """)]
    public partial Task<int> DeleteVersionedAsync(
        string sagaType, string correlationKey, byte[] expectedRowVersion, IAsyncDbTransaction tx, CancellationToken ct);
}
