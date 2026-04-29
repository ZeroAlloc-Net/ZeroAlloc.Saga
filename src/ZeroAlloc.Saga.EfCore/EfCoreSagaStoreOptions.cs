namespace ZeroAlloc.Saga.EfCore;

/// <summary>
/// EF Core-specific tunables for <see cref="EfCoreSagaStore{TSaga,TKey}"/>.
/// Inherits the backend-agnostic <see cref="SagaRetryOptions"/> so the
/// generator-emitted notification handlers (which inject the base type) see
/// the user-supplied values from <c>WithEfCoreStore&lt;TContext&gt;()</c>'s
/// configurator.
/// </summary>
public sealed class EfCoreSagaStoreOptions : SagaRetryOptions
{
}
