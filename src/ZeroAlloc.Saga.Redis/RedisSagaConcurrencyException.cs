using System;

namespace ZeroAlloc.Saga.Redis;

/// <summary>
/// Thrown by <see cref="RedisSagaStore{TSaga,TKey}"/> when an OCC mismatch is
/// detected — either the watched key changed between the load and the EXEC,
/// or an INSERT raced with another writer creating the same correlation key.
/// </summary>
/// <remarks>
/// Implements <see cref="ZeroAlloc.Saga.ISagaConcurrencyConflict"/>, so the
/// generator-emitted retry loop drives a Redis OCC clash down the same
/// scope-per-attempt path as any other backend conflict.
/// </remarks>
// Auxiliary ctors (parameterless / message / message+inner) are intentionally
// omitted — the framework-convention ctors would produce misleading instances
// without the load-bearing Key context.
#pragma warning disable RCS1194
public sealed class RedisSagaConcurrencyException : Exception, ZeroAlloc.Saga.ISagaConcurrencyConflict
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
