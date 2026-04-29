using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.Saga.Generator.Tests;

/// <summary>
/// Helper that runs <see cref="SagaGenerator"/> against a piece of source code and
/// returns the resulting <see cref="GeneratorDriver"/> for snapshot verification.
/// </summary>
internal static class GeneratorTestHost
{
    private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

    public static GeneratorDriver Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: "Saga.Snapshot",
            syntaxTrees: new[] { syntaxTree },
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new SagaGenerator())
            .RunGenerators(compilation);
        return driver;
    }

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        // System.Runtime + netcore reference assemblies that the test compilation needs.
        var trustedPlatformAssemblies = ((string?)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, System.StringSplitOptions.RemoveEmptyEntries);

        var refs = new List<MetadataReference>();
        foreach (var path in trustedPlatformAssemblies)
        {
            // Only include refs that look like BCL (avoid pulling in test-host assemblies).
            var name = System.IO.Path.GetFileName(path);
            if (name.StartsWith("System.", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "mscorlib.dll", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "netstandard.dll", System.StringComparison.OrdinalIgnoreCase))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // ZeroAlloc.Saga (attributes) and ZeroAlloc.Mediator (INotification, IRequest).
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
