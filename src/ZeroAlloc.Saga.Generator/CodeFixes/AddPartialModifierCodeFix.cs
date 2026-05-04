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
/// Code-fix provider for <c>ZASAGA016</c> — adds the <c>partial</c> modifier
/// to the offending step command type. Works for any
/// <see cref="TypeDeclarationSyntax"/> shape (class / struct / record /
/// record struct / interface), since step command types in the wild are
/// most commonly <c>record</c>s and <c>readonly record struct</c>s, not
/// classes. Distinct from <see cref="MakePartialCodeFixProvider"/>
/// (ZASAGA001), which targets only class declarations on the saga itself.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddPartialModifierCodeFix))]
[Shared]
public sealed class AddPartialModifierCodeFix : CodeFixProvider
{
    private const string DiagnosticId = "ZASAGA016";
    private const string Title = "Add 'partial' modifier";

    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var typeDecl = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (typeDecl is null) continue;

            // Already partial — nothing to do.
            if (typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))) continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedDocument: ct => AddPartialAsync(context.Document, typeDecl, ct),
                    equivalenceKey: Title),
                diagnostic);
        }
    }

    private static async Task<Document> AddPartialAsync(Document document, TypeDeclarationSyntax typeDecl, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return document;

        if (typeDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
        {
            return document;
        }

        var partialToken = SyntaxFactory.Token(SyntaxKind.PartialKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        TypeDeclarationSyntax newTypeDecl;
        if (typeDecl.Modifiers.Count == 0)
        {
            // Move the keyword's leading trivia onto 'partial' so we don't break formatting.
            partialToken = partialToken.WithLeadingTrivia(typeDecl.Keyword.LeadingTrivia);
            var newKeyword = typeDecl.Keyword.WithLeadingTrivia(SyntaxFactory.TriviaList());
            newTypeDecl = typeDecl
                .WithKeyword(newKeyword)
                .WithModifiers(SyntaxFactory.TokenList(partialToken));
        }
        else
        {
            newTypeDecl = typeDecl.WithModifiers(typeDecl.Modifiers.Add(partialToken));
        }

        var newRoot = root.ReplaceNode(typeDecl, newTypeDecl);
        return document.WithSyntaxRoot(newRoot);
    }
}
