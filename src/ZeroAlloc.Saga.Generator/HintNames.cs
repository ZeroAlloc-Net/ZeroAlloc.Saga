using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Hint names of generated files, following ZeroAlloc.Mapping's <c>HintNames</c>.
/// </summary>
/// <remarks>
/// A saga's files are named <c>{saga}.g.cs</c>, <c>{saga}.Fsm.g.cs</c>,
/// <c>{saga}.CorrelationDispatch.g.cs</c>, <c>{saga}.BuilderExtensions.g.cs</c>,
/// <c>{saga}.PersistableState.g.cs</c> and, per event, <c>{saga}.Handler.{event}.g.cs</c>, where
/// <c>{saga}</c> and <c>{event}</c> come from <see cref="ForType"/>. Naming them from simple names
/// made two same-named sagas, or two same-named events of one saga, throw a duplicate hint name,
/// and then no saga in the project was generated, issue #215. A dot followed by a fixed word
/// after a type's name cannot be mistaken for another type's name, because C# does not allow a
/// namespace and a type with the same full name in one compilation.
/// </remarks>
internal static class HintNames
{
    /// <summary>
    /// A name for <paramref name="type"/> that is unique within the compilation: the namespace,
    /// then the containing types and the type joined by <c>+</c>, each with its arity, and the
    /// type arguments of a constructed generic type in brackets, as in
    /// <c>App.Outer`1+Placed</c> or <c>App.Envelope`1[System.Int32]</c>. A type in the global
    /// namespace has no namespace part.
    /// </summary>
    /// <remarks>
    /// Nesting is written with <c>+</c> rather than a dot, so a type nested in <c>App.Outer</c>
    /// and a type at the top of namespace <c>App.Outer</c> never share a name. Roslyn compares
    /// hint names ignoring case, so names that differ only in case still collide.
    /// </remarks>
    public static string ForType(ITypeSymbol type)
    {
        var sb = new StringBuilder();
        AppendType(sb, type);
        return Sanitize(sb.ToString());
    }

    /// <summary>
    /// Keeps the characters an identifier, a namespace separator, an arity or a type argument
    /// list is written with, and escapes every other UTF-16 code unit as <c>-uXXXX</c>. No
    /// identifier contains a <c>-</c>, so an escaped name never collides with a name that
    /// needed no escaping.
    /// </summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is '.' or '+' or '`' or '[' or ']' or ',')
            {
                sb.Append(c);
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]) &&
                IsIdentifierCategory(CharUnicodeInfo.GetUnicodeCategory(name, i)))
            {
                sb.Append(c).Append(name[i + 1]);
                i++;
                continue;
            }

            if (!char.IsSurrogate(c) && IsIdentifierCategory(CharUnicodeInfo.GetUnicodeCategory(c)))
            {
                sb.Append(c);
                continue;
            }

            sb.Append("-u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static void AppendType(StringBuilder sb, ITypeSymbol type)
    {
        switch (type)
        {
            case INamedTypeSymbol named:
                AppendNamespace(sb, named.ContainingNamespace);
                AppendTypeChain(sb, named);
                break;
            case IArrayTypeSymbol array:
                AppendType(sb, array.ElementType);
                sb.Append('[').Append(',', array.Rank - 1).Append(']');
                break;
            default:
                // A type parameter, which only a generic saga (ZASAGA002) can use, or another
                // type no event is declared as.
                sb.Append(type.Name);
                break;
        }
    }

    private static void AppendNamespace(StringBuilder sb, INamespaceSymbol? ns)
    {
        if (ns is null || ns.IsGlobalNamespace) return;
        AppendNamespace(sb, ns.ContainingNamespace);
        sb.Append(ns.Name).Append('.');
    }

    private static void AppendTypeChain(StringBuilder sb, INamedTypeSymbol type)
    {
        if (type.ContainingType is { } outer)
        {
            AppendTypeChain(sb, outer);
            sb.Append('+');
        }
        sb.Append(type.Name);
        if (type.Arity == 0) return;

        sb.Append('`').Append(type.Arity.ToString(CultureInfo.InvariantCulture));
        if (SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition)) return;

        sb.Append('[');
        for (var i = 0; i < type.TypeArguments.Length; i++)
        {
            if (i > 0) sb.Append(',');
            AppendType(sb, type.TypeArguments[i]);
        }
        sb.Append(']');
    }

    private static bool IsIdentifierCategory(UnicodeCategory category) => category is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
        UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber or
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;
}
