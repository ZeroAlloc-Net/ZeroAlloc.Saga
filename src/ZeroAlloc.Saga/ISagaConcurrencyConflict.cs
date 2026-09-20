namespace ZeroAlloc.Saga;

/// <summary>
/// Marks an exception as an optimistic-concurrency conflict that the
/// generator-emitted saga handler should retry rather than propagate.
/// </summary>
/// <remarks>
/// <para>
/// A store signals "another writer won this round, reload and try again" by
/// throwing an exception that implements this interface. The retry loop in the
/// generated handler tests for it directly, so any store — in this repository or
/// not — participates without the generator knowing the type exists.
/// </para>
/// <para>
/// This replaces a list of fully-qualified type names compiled into the
/// generator. That list worked for the backends shipped here, but it meant a new
/// store could not join the retry path without editing
/// <c>HandlerEmitter</c>: the conflict was thrown, matched nothing, and escaped
/// as an ordinary exception. Nothing failed loudly — the saga simply lost the
/// update it should have retried.
/// </para>
/// <para>
/// The interface carries no members. It exists to be tested for, and a store's
/// conflict exception should carry whatever detail is useful in its own type.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class MyStoreConflictException : Exception, ISagaConcurrencyConflict
/// {
///     public MyStoreConflictException(string message, Exception? inner)
///         : base(message, inner) { }
/// }
/// </code>
/// </example>
public interface ISagaConcurrencyConflict
{
}
