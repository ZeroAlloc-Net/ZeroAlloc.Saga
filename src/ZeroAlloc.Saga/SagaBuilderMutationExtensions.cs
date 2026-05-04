namespace ZeroAlloc.Saga;

/// <summary>
/// Helper used by saga backend packages to flip the EfCore-backend flag on a
/// <see cref="ISagaBuilder"/>. Throws if the implementation does not also
/// implement <see cref="ISagaBuilderMutable"/> — i.e. a custom builder has
/// been substituted that opted out of the mutator contract.
/// </summary>
public static class SagaBuilderMutationExtensions
{
    /// <summary>Flips the EfCore-backend flag on the builder. Throws if the
    /// Redis backend is also configured (mutually exclusive).</summary>
    public static void SetEfCoreBackend(this ISagaBuilder builder)
    {
        var mutable = AsMutable(builder);
        if (builder.IsRedisBackend)
        {
            throw new System.InvalidOperationException(
                "WithEfCoreStore() cannot be combined with WithRedisStore() on the same ISagaBuilder; choose one durable backend per saga registration.");
        }
        mutable.IsEfCoreBackend = true;
    }

    /// <summary>Flips the Redis-backend flag on the builder. Throws if the
    /// EfCore backend is also configured (mutually exclusive).</summary>
    public static void SetRedisBackend(this ISagaBuilder builder)
    {
        var mutable = AsMutable(builder);
        if (builder.IsEfCoreBackend)
        {
            throw new System.InvalidOperationException(
                "WithRedisStore() cannot be combined with WithEfCoreStore() on the same ISagaBuilder; choose one durable backend per saga registration.");
        }
        mutable.IsRedisBackend = true;
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
