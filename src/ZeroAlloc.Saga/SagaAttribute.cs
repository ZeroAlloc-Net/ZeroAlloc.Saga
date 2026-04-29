namespace ZeroAlloc.Saga;

/// <summary>
/// Marks a partial class as a saga. The source generator emits the FSM,
/// per-event notification handlers, correlation dispatch, and AOT-safe
/// DI registrations for every type marked with this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SagaAttribute : Attribute
{
}
