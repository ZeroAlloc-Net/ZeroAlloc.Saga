namespace ZeroAlloc.Saga;

/// <summary>
/// Marks a method on a saga class as a forward step. The method must accept
/// a single notification (the input event) and return a command that the
/// generated handler will dispatch via IMediator.Send.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class StepAttribute : Attribute
{
    /// <summary>
    /// 1-based step ordering. Steps run in ascending Order. Each Order value
    /// must be unique within a saga and must form a contiguous sequence
    /// starting at 1.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Name of a parameterless method on the same saga class that produces
    /// the compensating command for this step. When unset, this step has no
    /// compensation and is skipped during the reverse cascade.
    /// </summary>
    public string? Compensate { get; init; }

    /// <summary>
    /// Optional notification type that triggers compensation when received
    /// after this step has run. Sets the failure entry point for the cascade
    /// starting at this step.
    /// </summary>
    public Type? CompensateOn { get; init; }
}
