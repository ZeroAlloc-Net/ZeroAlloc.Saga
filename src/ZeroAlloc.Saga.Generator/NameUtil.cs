using System.Collections.Generic;

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
