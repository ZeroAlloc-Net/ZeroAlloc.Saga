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
        var driver = CSharpGeneratorDriver.Create(new SagaGenerator())
            .RunGenerators(CreateCompilation(source));
        return driver;
    }

    /// <summary>
    /// Runs <see cref="SagaGenerator"/> and returns the compilation with the generated sources
    /// added, so a test can assert the generated code compiles. The source must declare what
    /// the Mediator and Serialisation generators would otherwise emit, such as <c>IMediator</c>.
    /// </summary>
    public static Compilation RunAndCompile(string source)
    {
        CSharpGeneratorDriver.Create(new SagaGenerator())
            .RunGeneratorsAndUpdateCompilation(CreateCompilation(source), out var output, out _);
        return output;
    }

    private static CSharpCompilation CreateCompilation(string source)
        => CSharpCompilation.Create(
            assemblyName: "Saga.Snapshot",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

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

        // What the generated handlers, builder extensions and registry call into, so
        // RunAndCompile can compile the generated code.
        AddAssembly(refs, typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly);
        AddAssembly(refs, typeof(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions).Assembly);
        AddAssembly(refs, typeof(Microsoft.Extensions.Logging.ILogger).Assembly);
        AddAssembly(refs, typeof(Microsoft.Extensions.Logging.LoggerExtensions).Assembly);

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
