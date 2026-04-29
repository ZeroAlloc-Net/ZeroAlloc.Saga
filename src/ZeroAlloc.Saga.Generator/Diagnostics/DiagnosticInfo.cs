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
    {
        return new DiagnosticInfo(
            descriptor,
            LocationInfo.From(location),
            EquatableArray<string>.From(args));
    }

    public Diagnostic ToDiagnostic()
    {
        return Diagnostic.Create(Descriptor, Location?.ToLocation(), MessageArgs.ToArray());
    }
}

/// <summary>
/// Equality-friendly capture of a <see cref="Microsoft.CodeAnalysis.Location"/>
/// for the source-generator pipeline.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public static LocationInfo? From(Location? location)
    {
        if (location is null) return null;
        var lineSpan = location.GetLineSpan();
        return new LocationInfo(
            location.SourceTree?.FilePath ?? string.Empty,
            location.SourceSpan,
            lineSpan.Span);
    }

    public Location ToLocation() => Microsoft.CodeAnalysis.Location.Create(FilePath, TextSpan, LineSpan);
}

/// <summary>
/// Immutable-array wrapper with structural equality so it composes with the
/// incremental-generator cache.
/// </summary>
internal readonly struct EquatableArray<T> : System.IEquatable<EquatableArray<T>>
{
    private readonly ImmutableArray<T> _values;

    public EquatableArray(ImmutableArray<T> values) => _values = values;

    public static EquatableArray<T> From(System.Collections.Generic.IEnumerable<T> source)
        => new(ImmutableArray.CreateRange(source));

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
