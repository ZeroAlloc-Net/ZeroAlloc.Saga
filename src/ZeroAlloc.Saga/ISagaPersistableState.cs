namespace ZeroAlloc.Saga;

/// <summary>
/// Implemented by every <see cref="SagaAttribute"/>-annotated class via
/// generator-emitted partial completion. Backends (in-memory, EF Core, Redis,
/// etc.) use this interface for byte-level state persistence and FSM-state
/// round-trip across process boundaries.
/// </summary>
/// <remarks>
/// User saga classes do not implement this interface manually — the
/// <c>ZeroAlloc.Saga.Generator</c> emits the implementation as part of
/// the partial-class completion file. The serialized format is opaque
/// and not guaranteed stable across major versions; a leading version
/// byte allows backends to detect mismatched format versions and surface
/// <see cref="SagaStateVersionMismatchException"/>.
/// </remarks>
public interface ISagaPersistableState
{
    /// <summary>Serializes the saga's instance state to a byte array.</summary>
    byte[] Snapshot();

    /// <summary>Restores the saga's instance state from a byte sequence.</summary>
    void Restore(System.ReadOnlySpan<byte> data);

    /// <summary>The current FSM state, by name. Used to round-trip via persistence.</summary>
    string CurrentFsmStateName { get; }

    /// <summary>Sets the FSM state from its name. Used to rehydrate after load.</summary>
    void SetFsmStateFromName(string stateName);
}
