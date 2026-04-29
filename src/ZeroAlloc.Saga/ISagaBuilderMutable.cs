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
}
