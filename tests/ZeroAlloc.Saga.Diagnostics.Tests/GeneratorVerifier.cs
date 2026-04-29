using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.Saga.Diagnostics.Tests;

/// <summary>
/// Lightweight harness that runs <see cref="ZeroAlloc.Saga.Generator.SagaGenerator"/>
/// against a given source string and returns the raw <see cref="Diagnostic"/>s
/// the generator reported. We bypass the full <c>SourceGeneratorTest</c> machinery
/// because it tries to compare emitted source files, which is overkill for a pure
/// diagnostic assertion.
/// </summary>
internal static class GeneratorVerifier
{
    private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

    public static Task<ImmutableArrayWrapper> RunAsync(string source, CancellationToken ct = default)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var compilation = CSharpCompilation.Create(
            assemblyName: "Saga.Diag.Test",
            syntaxTrees: new[] { syntaxTree },
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new ZeroAlloc.Saga.Generator.SagaGenerator())
            .RunGenerators(compilation, ct);

        var result = driver.GetRunResult();
        return Task.FromResult(new ImmutableArrayWrapper(result.Diagnostics));
    }

    public static async Task ExpectAsync(string source, params string[] diagnosticIds)
    {
        var run = await RunAsync(source).ConfigureAwait(false);
        var actual = new List<string>();
        foreach (var d in run.Diagnostics)
        {
            actual.Add(d.Id);
        }

        foreach (var expected in diagnosticIds)
        {
            Assert.Contains(expected, actual);
        }
    }

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var trustedPlatformAssemblies = ((string?)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, System.StringSplitOptions.RemoveEmptyEntries);

        var refs = new List<MetadataReference>();
        foreach (var path in trustedPlatformAssemblies)
        {
            var name = System.IO.Path.GetFileName(path);
            if (name.StartsWith("System.", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "mscorlib.dll", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "netstandard.dll", System.StringComparison.OrdinalIgnoreCase))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        AddAssembly(refs, typeof(ZeroAlloc.Saga.SagaAttribute).Assembly);
        AddAssembly(refs, typeof(ZeroAlloc.Mediator.INotification).Assembly);

        return refs;
    }

    private static void AddAssembly(List<MetadataReference> refs, Assembly assembly)
    {
        if (!string.IsNullOrEmpty(assembly.Location))
        {
            refs.Add(MetadataReference.CreateFromFile(assembly.Location));
        }
    }
}

/// <summary>Wrapper to expose generator diagnostics for assertions.</summary>
internal readonly struct ImmutableArrayWrapper
{
    public System.Collections.Immutable.ImmutableArray<Diagnostic> Diagnostics { get; }
    public ImmutableArrayWrapper(System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
    }
}
