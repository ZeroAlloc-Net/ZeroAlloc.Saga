namespace ZeroAlloc.Saga;

/// <summary>
/// Backend-supplied registrar that performs per-saga closed-generic
/// <see cref="ISagaStore{TSaga,TKey}"/> registration. Installed via
/// <see cref="SagaStoreRegistrar.SetTypedRegistrar(ISagaStoreRegistrar)"/>
/// from inside a backend's <c>WithEfCoreStore&lt;TContext&gt;()</c> (or
/// equivalent) builder extension; invoked by generator-emitted
/// <c>WithXxxSaga()</c> once per saga so each registration is a
/// closed-generic dispatch — AOT-safe, no <c>MakeGenericType</c>.
/// </summary>
public interface ISagaStoreRegistrar
{
    /// <summary>
    /// Replace the default in-memory <see cref="ISagaStore{TSaga,TKey}"/>
    /// registration on <paramref name="builder"/> with the backend's
    /// concrete store. Implementations typically call
    /// <see cref="SagaStoreRegistrar.OverrideStore{TSaga,TKey,TStore}"/>.
    /// </summary>
    void Register<TSaga, TKey>(ISagaBuilder builder)
        where TSaga : class, new()
        where TKey : notnull, System.IEquatable<TKey>;
}
