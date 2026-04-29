using Microsoft.Extensions.DependencyInjection;

namespace ZeroAlloc.Saga;

/// <summary>
/// Returned by <see cref="SagaServiceCollectionExtensions.AddSaga"/> and
/// extended by generator-emitted <c>AddXxxSaga(this ISagaBuilder)</c>
/// methods. Provides typed access to the underlying
/// <see cref="IServiceCollection"/>.
/// </summary>
public interface ISagaBuilder
{
    /// <summary>The underlying service collection being configured.</summary>
    IServiceCollection Services { get; }
}
