namespace ZeroAlloc.Saga;

/// <summary>
/// Backend-agnostic retry tunables for OCC-style conflicts in
/// generator-emitted notification handlers. Registered with sensible
/// defaults by <see cref="SagaServiceCollectionExtensions.AddSaga"/>;
/// durable-backend extensions (<c>WithEfCoreStore&lt;TContext&gt;</c>, etc.)
/// override the registration so user-supplied tweaks take effect.
/// </summary>
/// <remarks>
/// The InMemory backend never throws concurrency exceptions, so the retry
/// budget is dormant for it — but the type is registered unconditionally
/// so generator-emitted handlers compile against a single shape regardless
/// of backend choice.
/// </remarks>
public class SagaRetryOptions
{
    /// <summary>Maximum number of retries on a concurrency conflict. Default: 3.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base delay between retry attempts. Default: 50 ms.</summary>
    public System.TimeSpan RetryBaseDelay { get; set; } = System.TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// When <see langword="true"/>, the delay for attempt N is
    /// <c>RetryBaseDelay * 2^N</c>; when <see langword="false"/>, the
    /// delay is constant. Default: <see langword="true"/>.
    /// </summary>
    public bool UseExponentialBackoff { get; set; } = true;
}
