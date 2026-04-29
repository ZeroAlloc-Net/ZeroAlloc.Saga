namespace ZeroAlloc.Saga;

/// <summary>
/// Marks a property or field on a <c>[Saga]</c> class as NOT part of saga state.
/// Excluded from generator-emitted <c>Snapshot()</c> / <c>Restore()</c> methods.
/// </summary>
/// <remarks>
/// Use for diagnostic helpers, computed properties, or transient state that
/// should not be persisted to durable backends. Fields/properties marked with
/// this attribute are also exempt from <c>ZASAGA014</c> "unsupported field type"
/// reporting — the escape hatch for state shapes the v1.1 byte serializer
/// does not natively support.
/// </remarks>
[System.AttributeUsage(
    System.AttributeTargets.Field | System.AttributeTargets.Property,
    AllowMultiple = false,
    Inherited = false)]
public sealed class NotSagaStateAttribute : System.Attribute
{
}
