namespace ZeroAlloc.Saga;

/// <summary>
/// Helper used by saga backend packages to record that a durable store has been
/// configured. Throws if the builder does not also implement
/// <see cref="ISagaBuilderMutable"/> — i.e. a custom builder has been
/// substituted that opted out of the mutator contract.
/// </summary>
public static class SagaBuilderMutationExtensions
{
    /// <summary>
    /// Records that a durable store has been configured on the builder.
    /// </summary>
    /// <param name="builder">The saga builder being configured.</param>
    /// <param name="storeName">
    /// The calling extension's name, used only to phrase the error when a second
    /// store is configured — for example <c>"WithOrmStore()"</c>.
    /// </param>
    /// <exception cref="System.InvalidOperationException">
    /// A durable store is already configured. Exactly one is allowed per
    /// builder: the stores register the same closed-generic
    /// <see cref="ISagaStore{TSaga,TKey}"/> services, so a second would either
    /// lose to the first or silently replace it depending on registration order.
    /// </exception>
    /// <remarks>
    /// This replaced <c>SetEfCoreBackend</c> and <c>SetRedisBackend</c>, one per
    /// backend, each re-implementing the same mutual-exclusion check against the
    /// other. A new store had to add a third. The rule is the same for every
    /// backend, so it is expressed once here.
    /// </remarks>
    public static void SetDurableStore(this ISagaBuilder builder, string storeName)
    {
        System.ArgumentNullException.ThrowIfNull(builder);

        var mutable = AsMutable(builder);
        if (builder.HasDurableStore)
        {
            throw new System.InvalidOperationException(
                $"{storeName} cannot be combined with another durable store on the same " +
                "ISagaBuilder; choose one durable store per saga registration.");
        }

        mutable.HasDurableStore = true;
    }

    private static ISagaBuilderMutable AsMutable(ISagaBuilder builder)
    {
        if (builder is not ISagaBuilderMutable mutable)
        {
            throw new System.InvalidOperationException(
                "Custom ISagaBuilder implementations must also implement ISagaBuilderMutable.");
        }
        return mutable;
    }
}
