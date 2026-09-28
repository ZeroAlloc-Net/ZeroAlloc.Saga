using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ZeroAlloc.Saga.Generator.Diagnostics;

/// <summary>
/// Pipeline-friendly diagnostic record. Incremental source generators should not
/// cache <see cref="Diagnostic"/> instances directly because they don't have
/// structural equality; we capture descriptor + location + message args here and
/// materialize the actual <see cref="Diagnostic"/> at report time.
/// </summary>
internal sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    LocationInfo? Location,
    EquatableArray<string> MessageArgs)
{
    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params string[] args)
        => Create(descriptor, LocationInfo.From(location), args);

    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] args)
    {
        return new DiagnosticInfo(
            descriptor,
            location,
            EquatableArray<string>.From(args));
    }

    public Diagnostic ToDiagnostic()
    {
        return Diagnostic.Create(Descriptor, Location?.ToLocation(), MessageArgs.ToArray());
    }
}

/// <summary>
/// A diagnostic location the pipeline can cache: the syntax tree and the span within it.
/// </summary>
/// <remarks>
/// <para>
/// The tree is kept, not just its file path, because the rebuilt diagnostic must be a source
/// location. <c>Location.Create(filePath, span, lineSpan)</c> gives an external-file location
/// with no <see cref="Location.SourceTree"/>, and the compiler then ignores
/// <c>#pragma warning disable</c> for it, so a ZASAGA warning could not be suppressed at its site.
/// </para>
/// <para>
/// Keeping the tree does not defeat caching. <see cref="SyntaxTree"/> compares by reference, and
/// a compilation reuses the tree instance of every file that did not change, so the location
/// compares equal across runs until its own file is edited, when the model is rebuilt anyway. A
/// tree belongs to no one compilation, and only the tree of the latest run is held.
/// </para>
/// </remarks>
internal sealed record LocationInfo(SyntaxTree Tree, TextSpan Span)
{
    public Location ToLocation() => Location.Create(Tree, Span);

    /// <summary>
    /// Null for <see cref="Location.None"/>, a missing location, or one outside source, which
    /// report as <see cref="Location.None"/>. Every location the generator reports is in source.
    /// </summary>
    public static LocationInfo? From(Location? location) =>
        location?.SourceTree is { } tree ? new LocationInfo(tree, location.SourceSpan) : null;
}

/// <summary>
/// Immutable-array wrapper with structural equality so it composes with the
/// incremental-generator cache.
/// </summary>
internal readonly struct EquatableArray<T> : System.IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    private readonly ImmutableArray<T> _values;

    public EquatableArray(ImmutableArray<T> values) => _values = values;

    public static EquatableArray<T> From(System.Collections.Generic.IEnumerable<T> source)
        => new(ImmutableArray.CreateRange(source));

    public int Count => _values.IsDefault ? 0 : _values.Length;

    public T this[int index] => _values[index];

    public ImmutableArray<T>.Enumerator GetEnumerator()
        => (_values.IsDefault ? ImmutableArray<T>.Empty : _values).GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator()
        => ((IEnumerable<T>)(_values.IsDefault ? ImmutableArray<T>.Empty : _values)).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        => ((IEnumerable<T>)this).GetEnumerator();

    public T[] ToArray() => _values.IsDefault ? System.Array.Empty<T>() : System.Linq.Enumerable.ToArray(_values);

    public bool Equals(EquatableArray<T> other)
    {
        if (_values.IsDefault) return other._values.IsDefault;
        if (other._values.IsDefault) return false;
        if (_values.Length != other._values.Length) return false;
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < _values.Length; i++)
        {
            if (!cmp.Equals(_values[i], other._values[i])) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_values.IsDefault) return 0;
        unchecked
        {
            int hash = 17;
            foreach (var v in _values)
            {
                hash = hash * 31 + (v?.GetHashCode() ?? 0);
            }
            return hash;
        }
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}
