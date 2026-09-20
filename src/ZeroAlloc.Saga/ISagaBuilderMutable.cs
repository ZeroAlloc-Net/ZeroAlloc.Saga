namespace ZeroAlloc.Saga;

/// <summary>
/// Mutator contract used by saga backend packages to set
/// <see cref="ISagaBuilder.HasDurableStore"/> on the default builder. Public so
/// backend assemblies in their own NuGet package can reach it without
/// <c>InternalsVisibleTo</c>.
/// </summary>
/// <remarks>
/// The default <see cref="ISagaBuilder"/> returned by
/// <see cref="SagaServiceCollectionExtensions.AddSaga"/> implements both
/// interfaces. A custom builder that wants to accept a durable store must
/// implement this one as well; the
/// <see cref="SagaBuilderMutationExtensions.SetDurableStore"/> helper throws if
/// the cast fails.
/// </remarks>
public interface ISagaBuilderMutable
{
    /// <summary>Settable mirror of <see cref="ISagaBuilder.HasDurableStore"/>.</summary>
    bool HasDurableStore { get; set; }
}
