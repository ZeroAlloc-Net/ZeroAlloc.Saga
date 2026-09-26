namespace ZeroAlloc.Saga.Outbox.Redis;

/// <summary>
/// Configuration for the Redis-native outbox store + the Redis saga unit of work.
/// </summary>
public sealed class RedisOutboxOptions
{
    /// <summary>
    /// Prefix prepended to outbox keys. Default <c>"saga-outbox"</c>. Three keys are derived
    /// from this prefix:
    /// <list type="bullet">
    ///   <item><description><c>{Prefix}:entry:{id}</c> — Hash with entry fields, including the lease holder <c>lockedBy</c> and expiry <c>lockedUntil</c>.</description></item>
    ///   <item><description><c>{Prefix}:pending</c> — Sorted Set, score = lease expiry while leased, else due time; member = id.</description></item>
    ///   <item><description><c>{Prefix}:succeeded</c> / <c>{Prefix}:deadletter</c> — Sets of finished entry ids.</description></item>
    /// </list>
    /// </summary>
    public string KeyPrefix { get; set; } = "saga-outbox";
}
