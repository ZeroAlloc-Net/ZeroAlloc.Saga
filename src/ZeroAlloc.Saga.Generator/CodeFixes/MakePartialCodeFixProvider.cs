using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroAlloc.Saga.Generator.CodeFixes;

/// <summary>
/// Code-fix provider for <c>ZASAGA001</c> — adds the <c>partial</c> modifier
/// to a <c>[Saga]</c>-annotated class declaration.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(MakePartialCodeFixProvider))]
[Shared]
public sealed class MakePartialCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "ZASAGA001";
    private const string Title = "Make class 'partial'";

    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var classDecl = node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl is null) continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedDocument: ct => MakePartialAsync(context.Document, classDecl, ct),
                    equivalenceKey: Title),
                diagnostic);
        }
    }

    private static async Task<Document> MakePartialAsync(Document document, ClassDeclarationSyntax classDecl, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return document;

        if (classDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            return document;
        }

        var partialToken = SyntaxFactory.Token(SyntaxKind.PartialKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        SyntaxTokenList newModifiers;
        if (classDecl.Modifiers.Count == 0)
        {
            // Move the keyword's leading trivia onto 'partial' so we don't break formatting.
            partialToken = partialToken.WithLeadingTrivia(classDecl.Keyword.LeadingTrivia);
            var newKeyword = classDecl.Keyword.WithLeadingTrivia(SyntaxFactory.TriviaList());
            var withModifier = classDecl
                .WithKeyword(newKeyword)
                .WithModifiers(SyntaxFactory.TokenList(partialToken));
            var newRoot1 = root.ReplaceNode(classDecl, withModifier);
            return document.WithSyntaxRoot(newRoot1);
        }
        else
        {
            newModifiers = classDecl.Modifiers.Add(partialToken);
        }

        var newClassDecl = classDecl.WithModifiers(newModifiers);
        var newRoot = root.ReplaceNode(classDecl, newClassDecl);
        return document.WithSyntaxRoot(newRoot);
    }
}
