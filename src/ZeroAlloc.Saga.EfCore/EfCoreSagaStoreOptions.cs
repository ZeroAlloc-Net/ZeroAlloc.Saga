namespace ZeroAlloc.Saga.EfCore;

/// <summary>
/// Tunables for <c>EfCoreSagaStore&lt;,&gt;</c> (PR 3). Controls retry
/// behaviour when the row-version concurrency check fails on save.
/// </summary>
public sealed class EfCoreSagaStoreOptions
{
    /// <summary>
    /// Maximum number of save retries after a row-version concurrency
    /// conflict. Default: 3.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay between retry attempts. With <see cref="UseExponentialBackoff"/>
    /// enabled, attempt N waits <c>RetryBaseDelay * 2^(N-1)</c>. Default: 50 ms.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Whether to apply exponential backoff between retry attempts. Default: true.
    /// </summary>
    public bool UseExponentialBackoff { get; set; } = true;
}
