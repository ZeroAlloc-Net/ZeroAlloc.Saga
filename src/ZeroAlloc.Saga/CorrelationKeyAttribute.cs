namespace ZeroAlloc.Saga;

/// <summary>
/// Marks a method on a saga class as a correlation-key extractor. The method
/// must accept a single notification and return the saga's correlation key
/// type. Every event the saga handles must have a matching correlation
/// method (or be reachable through one).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CorrelationKeyAttribute : Attribute
{
}
