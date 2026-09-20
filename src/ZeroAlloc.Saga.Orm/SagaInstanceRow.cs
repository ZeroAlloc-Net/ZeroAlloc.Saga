namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// The persisted shape of one saga instance, as materialised by
/// <see cref="SagaInstanceRepository"/>.
/// </summary>
/// <param name="State">Opaque serialised saga state, produced by <c>ISagaPersistableState.Snapshot()</c>.</param>
/// <param name="CurrentFsmState">The saga's current FSM state name.</param>
/// <param name="RowVersion">
/// The optimistic-concurrency token. Rotated on every write and used as the
/// <c>WHERE</c> predicate of the next one, so a write that finds it changed
/// affects zero rows and is reported as a conflict.
/// </param>
/// <remarks>
/// Only the three columns the store actually reads are selected. The
/// <c>CreatedAt</c>/<c>UpdatedAt</c> columns are written for operator
/// diagnostics and never read back, which keeps the read path free of
/// provider-specific temporal type mapping.
/// </remarks>
internal sealed record SagaInstanceRow(byte[] State, string CurrentFsmState, byte[] RowVersion);
