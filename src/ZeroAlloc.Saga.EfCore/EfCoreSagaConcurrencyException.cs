using Microsoft.EntityFrameworkCore;

namespace ZeroAlloc.Saga.EfCore;

/// <summary>
/// An EF Core concurrency failure, re-thrown so the saga retry loop can recognise
/// it through <see cref="ISagaConcurrencyConflict"/> rather than by type name.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="DbUpdateConcurrencyException"/> deliberately. EF Core's
/// exceptions are not ours to annotate, and callers that already catch
/// <see cref="DbUpdateConcurrencyException"/> — or its base
/// <see cref="DbUpdateException"/> — keep working unchanged. The original
/// exception is preserved as <see cref="System.Exception.InnerException"/>.
/// </para>
/// <para>
/// The alternative was leaving EF Core's type names hard-coded in the generator.
/// That is what made the retry path closed to any store shipped outside this
/// repository.
/// </para>
/// </remarks>
// Auxiliary ctors are intentionally omitted: this type is only ever constructed
// by the store when wrapping a concrete EF Core failure, and an instance without
// an inner exception would misrepresent what happened.
#pragma warning disable RCS1194
public sealed class EfCoreSagaConcurrencyException : DbUpdateConcurrencyException, ISagaConcurrencyConflict
#pragma warning restore RCS1194
{
    /// <summary>
    /// Wraps an EF Core update failure raised while persisting saga state.
    /// </summary>
    /// <param name="sagaType">The saga's discriminator, as stored in the row.</param>
    /// <param name="correlationKey">The correlation key of the contended instance.</param>
    /// <param name="inner">The EF Core exception that was raised.</param>
    public EfCoreSagaConcurrencyException(string sagaType, string correlationKey, DbUpdateException inner)
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
