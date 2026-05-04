namespace ZeroAlloc.Saga.Redis;

/// <summary>
/// Configuration knobs for <see cref="RedisSagaStore{TSaga,TKey}"/>. Extends
/// <see cref="SagaRetryOptions"/> so the generator-emitted handler's retry loop
/// reads the same surface for OCC retries regardless of backend.
/// </summary>
public sealed class RedisSagaStoreOptions : SagaRetryOptions
{
    /// <summary>
    /// Prefix prepended to every saga key. Default <c>"saga"</c>. Final key shape:
    /// <c>{KeyPrefix}:{SagaTypeName}:{CorrelationKey}</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "saga";
}
