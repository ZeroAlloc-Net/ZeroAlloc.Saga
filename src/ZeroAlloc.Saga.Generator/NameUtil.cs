using System.Collections.Generic;
using System.Linq;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Small string helpers shared by the emitters.
/// </summary>
internal static class NameUtil
{
    /// <summary>
    /// Returns the simple type name from a fully qualified name
    /// (last segment after the rightmost '.').
    /// </summary>
    public static string SimpleName(string fqn)
    {
        var idx = fqn.LastIndexOf('.');
        return idx >= 0 ? fqn.Substring(idx + 1) : fqn;
    }

    /// <summary>
    /// The name an event goes by in the identifiers generated for one saga: its handler class
    /// <c>{Saga}_{Name}_Handler</c> and its FSM trigger <c>Trigger.{Name}</c>. It is the event's
    /// simple name, unless another event of the same saga has the same simple name, as
    /// <c>A.Placed</c> and <c>B.Placed</c> do. Then it is the fully qualified name with every
    /// '.' replaced by '_', <c>A_Placed</c>, so each event keeps its own handler and trigger,
    /// #216. Every other saga's generated names stay as they were.
    /// </summary>
    public static string EventName(SagaModel model, string eventFqn)
        => IsSimpleNameShared(model, eventFqn) ? eventFqn.Replace('.', '_') : SimpleName(eventFqn);

    /// <summary>
    /// How generated log messages name an event: its simple name, or its fully qualified name when
    /// another event of the same saga has the same simple name.
    /// </summary>
    public static string EventLogName(SagaModel model, string eventFqn)
        => IsSimpleNameShared(model, eventFqn) ? eventFqn : SimpleName(eventFqn);

    private static bool IsSimpleNameShared(SagaModel model, string eventFqn)
    {
        var simple = SimpleName(eventFqn);
        return model.Steps.Select(s => s.EventTypeFqn)
            .Concat(model.CompensateOnEventFqns)
            .Any(other => !string.Equals(other, eventFqn, System.StringComparison.Ordinal)
                && string.Equals(SimpleName(other), simple, System.StringComparison.Ordinal));
    }
}

/// <summary>
/// Helpers for emitting C# type expressions in generated code.
/// </summary>
internal static class TypeNameHelper
{
    // Roslyn's SymbolDisplayFormat.FullyQualifiedFormat renders predefined
    // C# types as the keyword (e.g. "int", "string"). Concatenating
    // "global::" in front of these produces invalid C# (`global::int`),
    // so we must emit the bare keyword instead.
    private static readonly HashSet<string> CSharpPredefinedTypes = new(System.StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float",
        "int", "uint", "nint", "nuint", "long", "ulong", "short", "ushort",
        "object", "string", "void"
    };

    /// <summary>
    /// Returns a fully-qualified C# type expression for use in generated code.
    /// Predefined types (int, string, etc.) are returned bare; all other types
    /// get the "global::" prefix to avoid namespace ambiguity.
    /// </summary>
    public static string GlobalQualified(string fqn)
    {
        if (CSharpPredefinedTypes.Contains(fqn))
            return fqn;
        return "global::" + fqn;
    }
}
