namespace ZeroAlloc.Saga;

/// <summary>
/// Helper used by saga backend packages to flip the EfCore-backend flag on a
/// <see cref="ISagaBuilder"/>. Throws if the implementation does not also
/// implement <see cref="ISagaBuilderMutable"/> — i.e. a custom builder has
/// been substituted that opted out of the mutator contract.
/// </summary>
public static class SagaBuilderMutationExtensions
{
    /// <summary>Flips the EfCore-backend flag on the builder.</summary>
    public static void SetEfCoreBackend(this ISagaBuilder builder)
    {
        if (builder is not ISagaBuilderMutable mutable)
        {
            throw new System.InvalidOperationException(
                "Custom ISagaBuilder implementations must also implement ISagaBuilderMutable.");
        }
        mutable.IsEfCoreBackend = true;
    }
}
