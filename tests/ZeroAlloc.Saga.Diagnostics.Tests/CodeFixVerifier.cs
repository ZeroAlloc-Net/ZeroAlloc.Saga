using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace ZeroAlloc.Saga.Diagnostics.Tests;

/// <summary>
/// Test harness for ZeroAlloc.Saga code-fix providers. Builds an in-memory
/// AdHocWorkspace project, runs the SagaGenerator to surface a diagnostic,
/// asks the supplied <see cref="CodeFixProvider"/> for a fix, applies it, and
/// returns the resulting source. Tests assert the fixed source matches the
/// expected text.
/// </summary>
internal static class CodeFixVerifier
{
    private static readonly IReadOnlyList<MetadataReference> References = BuildReferences();

    public static async Task<string> ApplyFixAsync(string source, CodeFixProvider provider, string diagnosticId, CancellationToken ct = default)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "Saga.CodeFix.Test", "Saga.CodeFix.Test", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable))
            .AddMetadataReferences(projectId, References)
            .AddDocument(documentId, "Source.cs", SourceText.From(source));

        var document = solution.GetDocument(documentId)!;

        var diagnostic = await GetGeneratorDiagnosticAsync(document, diagnosticId, ct).ConfigureAwait(false);
        Assert.NotNull(diagnostic);

        // Map the diagnostic location into the live document.
        var localDiag = MapDiagnosticToDocument(document, diagnostic!, ct);

        var actions = new List<CodeAction>();
        var fixContext = new CodeFixContext(
            document,
            localDiag,
            (a, _) => actions.Add(a),
            ct);
        await provider.RegisterCodeFixesAsync(fixContext).ConfigureAwait(false);
        Assert.NotEmpty(actions);

        var operations = await actions[0].GetOperationsAsync(ct).ConfigureAwait(false);
        var solutionWithFix = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        var fixedDocument = solutionWithFix.GetDocument(documentId)!;
        var formatted = await Formatter.FormatAsync(fixedDocument, cancellationToken: ct).ConfigureAwait(false);
        var text = await formatted.GetTextAsync(ct).ConfigureAwait(false);
        return text.ToString();
    }

    private static async Task<Diagnostic?> GetGeneratorDiagnosticAsync(Document document, string diagnosticId, CancellationToken ct)
    {
        var compilation = await document.Project.GetCompilationAsync(ct).ConfigureAwait(false);
        Assert.NotNull(compilation);

        var driver = CSharpGeneratorDriver.Create(new ZeroAlloc.Saga.Generator.SagaGenerator())
            .RunGenerators(compilation!, ct);
        var result = driver.GetRunResult();

        return result.Diagnostics.FirstOrDefault(d => d.Id == diagnosticId);
    }

    private static Diagnostic MapDiagnosticToDocument(Document document, Diagnostic original, CancellationToken ct)
    {
        // The diagnostic from the generator carries a Location relative to the
        // syntax tree we used to build the compilation; in this harness it IS
        // the same syntax tree as the document, so we can pass it through.
        return original;
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
