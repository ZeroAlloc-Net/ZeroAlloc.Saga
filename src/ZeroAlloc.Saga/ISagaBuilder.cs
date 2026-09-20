using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Saga;

/// <summary>
/// Returned by <see cref="SagaServiceCollectionExtensions.AddSaga"/> and
/// extended by generator-emitted <c>WithXxxSaga(this ISagaBuilder)</c>
/// methods. Provides typed access to the underlying
/// <see cref="IServiceCollection"/>.
/// </summary>
public interface ISagaBuilder
{
    /// <summary>The underlying service collection being configured.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// True once a durable store has been configured on this builder — by
    /// <c>WithEfCoreStore</c>, <c>WithRedisStore</c>, <c>WithOrmStore</c> or any
    /// other backend package. False means the in-memory default is in effect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Backend packages set this through <see cref="ISagaBuilderMutable"/>, via
    /// the <see cref="SagaBuilderMutationExtensions.SetDurableStore"/> helper,
    /// which also enforces that only one durable store is configured per
    /// builder.
    /// </para>
    /// <para>
    /// This replaced a boolean per backend — <c>IsEfCoreBackend</c> and
    /// <c>IsRedisBackend</c>. Those enumerated the backends this package knew
    /// about, so shipping a new store meant editing this interface. What the
    /// composition path actually needs to know is whether a durable store is
    /// present at all; <em>which</em> one is supplied by
    /// <see cref="SagaStoreRegistrar"/>, which the backend installs itself.
    /// </para>
    /// <para>
    /// A backend that genuinely needs to identify another backend — the Redis
    /// outbox bridge enlists into the Redis store's MULTI/EXEC, so it must know
    /// the store really is Redis — should look for that backend's own
    /// registration in <see cref="Services"/> rather than expect a flag here.
    /// </para>
    /// </remarks>
    bool HasDurableStore { get; }
}
