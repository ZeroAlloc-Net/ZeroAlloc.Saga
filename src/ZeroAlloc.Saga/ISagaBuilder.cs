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
    /// True when a durable backend (e.g. <c>WithEfCoreStore</c>) has been
    /// configured on this builder. Read by generator-emitted
    /// <c>WithXxxSaga()</c> at composition time to choose between
    /// <c>InMemorySagaStore&lt;,&gt;</c> (default) and the durable backend's
    /// concrete store type.
    /// </summary>
    /// <remarks>
    /// Default <see langword="false"/>. Backend packages
    /// (<c>ZeroAlloc.Saga.EfCore</c>) flip this via the
    /// <see cref="ISagaBuilderMutable"/> contract.
    /// </remarks>
    bool IsEfCoreBackend { get; }

    /// <summary>
    /// True when <c>ZeroAlloc.Saga.Redis</c>'s <c>WithRedisStore</c> has been
    /// configured on this builder. Mutually exclusive with
    /// <see cref="IsEfCoreBackend"/>: calling both throws.
    /// </summary>
    /// <remarks>
    /// Default <see langword="false"/> via DIM so existing
    /// <see cref="ISagaBuilder"/> implementations from before
    /// <c>ZeroAlloc.Saga.Redis</c> shipped continue to compile and behave
    /// correctly. <c>ZeroAlloc.Saga.Redis</c> flips this via the
    /// <see cref="ISagaBuilderMutable"/> contract. The OCC retry path in the
    /// generator-emitted handler covers <c>RedisSagaConcurrencyException</c>
    /// alongside the EfCore exceptions.
    /// </remarks>
    bool IsRedisBackend => false;
}
