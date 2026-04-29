namespace ZeroAlloc.Saga;

/// <summary>
/// Thrown by generator-emitted <c>Restore()</c> when the persisted saga state's
/// leading version byte does not match the version the current build was
/// compiled to expect. This typically indicates that the saga's state shape
/// (fields/properties) changed between deploys and in-flight saga rows in the
/// durable backend predate the change.
/// </summary>
/// <remarks>
/// v1.x has no automatic migration path. Recovery options: drain in-flight
/// sagas before upgrading, or wait for state-shape migration support
/// (BACKLOG #14) in a later version.
/// </remarks>
// RCS1194: Roslynator suggests the framework-convention ctors (parameterless,
// message-only, message+inner). We deliberately omit those — this is a sealed
// exception with a strict three-argument contract; constructing it without
// SagaType/Expected/Actual would produce misleading instances.
#pragma warning disable RCS1194
public sealed class SagaStateVersionMismatchException : System.Exception
#pragma warning restore RCS1194
{
    public string SagaType { get; }
    public byte Expected { get; }
    public byte Actual { get; }

    public SagaStateVersionMismatchException(string sagaType, byte expected, byte actual)
        : base($"Saga state for '{sagaType}' was persisted with format version {actual}, " +
               $"but this build expects version {expected}. Drain in-flight sagas before " +
               $"upgrading, or wait for a future version with state migration support.")
    {
        SagaType = sagaType;
        Expected = expected;
        Actual = actual;
    }
}
