namespace ZeroAlloc.Saga;

/// <summary>
/// Mutator contract used by saga backend packages (Saga.EfCore, etc.) to
/// flip the <see cref="ISagaBuilder.IsEfCoreBackend"/> flag on the default
/// builder. Public so backend assemblies in their own NuGet package can
/// access it without InternalsVisibleTo.
/// </summary>
/// <remarks>
/// The default <see cref="ISagaBuilder"/> implementation returned by
/// <see cref="SagaServiceCollectionExtensions.AddSaga"/> implements both
/// interfaces. Custom builders that want to participate in backend
/// switching must implement this interface as well; the
/// <see cref="SagaBuilderMutationExtensions.SetEfCoreBackend"/> helper
/// throws <see cref="System.InvalidOperationException"/> if the cast fails.
/// </remarks>
public interface ISagaBuilderMutable
{
    /// <summary>Settable mirror of <see cref="ISagaBuilder.IsEfCoreBackend"/>.</summary>
    bool IsEfCoreBackend { get; set; }

    /// <summary>
    /// Settable mirror of <see cref="ISagaBuilder.IsRedisBackend"/>. Default
    /// <see langword="false"/> via DIM so existing
    /// <see cref="ISagaBuilderMutable"/> implementations from before
    /// <c>ZeroAlloc.Saga.Redis</c> shipped continue to compile. The setter
    /// throws <see cref="System.NotSupportedException"/> when not overridden,
    /// which surfaces a clear error if a custom builder participates in
    /// backend switching but hasn't migrated yet.
    /// </summary>
    bool IsRedisBackend
    {
        get => false;
        set => throw new System.NotSupportedException(
            "This ISagaBuilderMutable implementation does not support the Redis backend. " +
            "Override IsRedisBackend on your custom builder to participate in WithRedisStore() registrations.");
    }
}
