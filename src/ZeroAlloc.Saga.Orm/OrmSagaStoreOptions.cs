namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// Tuning for the ZeroAlloc.ORM saga backend. Inherits the shared
/// <see cref="SagaRetryOptions"/> so conflict-retry behaviour is configured the
/// same way as for every other durable store.
/// </summary>
/// <remarks>
/// There is deliberately no table-name setting. The ORM composes SQL at compile
/// time from the <c>[Query]</c>/<c>[Command]</c> attributes, so the table is
/// named literally in the emitted commands and a runtime property could not
/// reach it. A configurable name would have to be a no-op, and a setting that
/// silently does nothing is worse than its absence. Point the store at a
/// different table by supplying your own <c>IMigrationSource</c> and repository
/// instead.
/// </remarks>
public sealed class OrmSagaStoreOptions : SagaRetryOptions
{
}
