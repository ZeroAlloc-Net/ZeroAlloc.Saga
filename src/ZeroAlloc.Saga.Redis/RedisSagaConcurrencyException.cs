using System;

namespace ZeroAlloc.Saga.Redis;

/// <summary>
/// Thrown by <see cref="RedisSagaStore{TSaga,TKey}"/> when an OCC mismatch is
/// detected — either the watched key changed between the load and the EXEC,
/// or an INSERT raced with another writer creating the same correlation key.
/// </summary>
/// <remarks>
/// The generator-emitted saga handler's retry loop matches this type by
/// fully-qualified name (<c>ZeroAlloc.Saga.Redis.RedisSagaConcurrencyException</c>)
/// alongside the EfCore conflict exceptions, so a Redis OCC clash drives the
/// same scope-per-attempt retry path as an EfCore <c>DbUpdateConcurrencyException</c>.
/// </remarks>
// Auxiliary ctors (parameterless / message / message+inner) are intentionally
// omitted — the framework-convention ctors would produce misleading instances
// without the load-bearing Key context.
#pragma warning disable RCS1194
public sealed class RedisSagaConcurrencyException : Exception
#pragma warning restore RCS1194
{
    /// <summary>The Redis key that was being modified when the conflict was detected.</summary>
    public string Key { get; }

    public RedisSagaConcurrencyException(string key)
        : base($"Redis saga state at key '{key}' was modified concurrently.")
    {
        Key = key;
    }

    public RedisSagaConcurrencyException(string key, string message)
        : base(message)
    {
        Key = key;
    }
}
