namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// The names <see cref="SagaGenerator"/> gives its pipeline steps, so tests can assert that an
/// unrelated edit leaves each of them cached.
/// </summary>
internal static class TrackingNames
{
    public const string SagaModels = nameof(SagaModels);
    public const string AllSagaModels = nameof(AllSagaModels);
    public const string SerialisationReferenced = nameof(SerialisationReferenced);
    public const string DurableBackendReferenced = nameof(DurableBackendReferenced);
}
