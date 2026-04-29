namespace ZeroAlloc.Saga.EfCore;

/// <summary>
/// EF Core entity that maps a single saga instance to a row in the shared
/// <c>SagaInstance</c> table. The composite primary key is
/// <c>(SagaType, CorrelationKey)</c> so multiple saga types can coexist in
/// one table without identifier collisions; <see cref="RowVersion"/> drives
/// optimistic concurrency control on save.
/// </summary>
/// <remarks>
/// Real persistence behaviour is supplied by <c>EfCoreSagaStore&lt;,&gt;</c>
/// (lands in PR 3 of the Phase 2 EfCore campaign). This entity, plus the
/// model configuration in <see cref="SagaModelBuilderExtensions.AddSagas"/>,
/// is the public schema contract — pinned in this PR so users can already
/// generate migrations.
/// </remarks>
public sealed class SagaInstanceEntity
{
    /// <summary>Fully-qualified saga type name (e.g. <c>MyApp.OrderSaga</c>).</summary>
    public string SagaType { get; set; } = "";

    /// <summary>Stringified correlation key (the saga's <c>TKey</c> value).</summary>
    public string CorrelationKey { get; set; } = "";

    /// <summary>Opaque serialized saga state, produced by <c>ISagaPersistableState.Snapshot</c>.</summary>
    public byte[] State { get; set; } = Array.Empty<byte>();

    /// <summary>Current FSM state name, round-tripped via persistence.</summary>
    public string CurrentFsmState { get; set; } = "";

    /// <summary>Row-version token used for optimistic concurrency on save.</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();

    /// <summary>UTC timestamp at which this saga instance was first persisted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the most recent successful save.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
