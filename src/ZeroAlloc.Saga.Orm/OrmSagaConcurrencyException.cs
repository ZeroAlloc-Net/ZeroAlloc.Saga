namespace ZeroAlloc.Saga.Orm;

/// <summary>
/// Thrown when another writer changed a saga row first. Implements
/// <see cref="ISagaConcurrencyConflict"/>, so the generator-emitted handler
/// reloads and retries rather than letting the update escape.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole reason the marker interface exists. The generator used to
/// recognise conflicts by a fixed list of fully-qualified type names, which no
/// store outside that list could join — a backend like this one would throw,
/// match nothing, and silently lose the update it should have retried.
/// </para>
/// <para>
/// Two situations produce one: an <c>UPDATE ... WHERE RowVersion = @expected</c>
/// that affects zero rows, and an <c>INSERT</c> that loses a race to another
/// writer creating the same instance.
/// </para>
/// </remarks>
// Auxiliary ctors are intentionally omitted: this type is only ever constructed
// by the store when it has a concrete saga identity to report, and an instance
// without one would misrepresent what happened. Mirrors the EF Core backend.
#pragma warning disable RCS1194
public sealed class OrmSagaConcurrencyException : Exception, ISagaConcurrencyConflict
#pragma warning restore RCS1194
{
    /// <summary>
    /// Creates a conflict for a contended saga instance.
    /// </summary>
    /// <param name="sagaType">The saga's discriminator, as stored in the row.</param>
    /// <param name="correlationKey">The correlation key of the contended instance.</param>
    /// <param name="inner">The underlying provider exception, when there was one.</param>
    public OrmSagaConcurrencyException(string sagaType, string correlationKey, Exception? inner = null)
        : base($"Concurrency conflict persisting saga '{sagaType}' with correlation key " +
               $"'{correlationKey}'. Another writer changed the row first; the saga handler " +
               $"will reload and retry.", inner)
    {
        SagaType = sagaType;
        CorrelationKey = correlationKey;
    }

    /// <summary>The saga's discriminator, as stored in the row.</summary>
    public string SagaType { get; }

    /// <summary>The correlation key of the contended saga instance.</summary>
    public string CorrelationKey { get; }
}
