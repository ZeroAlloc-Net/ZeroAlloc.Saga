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
using Microsoft.CodeAnalysis.Formatting;

namespace ZeroAlloc.Saga.Generator.CodeFixes;

/// <summary>
/// Code-fix provider for <c>ZASAGA009</c> — adds a stub compensation method
/// matching the name referenced by <c>[Step.Compensate = nameof(X)]</c>.
/// The stub returns <c>default!</c> of the same return type pattern as the
/// step's command (we fall back to <c>object?</c> when we cannot infer it).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddMissingCompensationMethodCodeFixProvider))]
[Shared]
public sealed class AddMissingCompensationMethodCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "ZASAGA009";
    private const string Title = "Add missing compensation method";

    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var stepMethod = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var classDecl = node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (stepMethod is null || classDecl is null) continue;

            // Find the Compensate = nameof(X) argument to learn the method name.
            string? compensateName = null;
            foreach (var list in stepMethod.AttributeLists)
            {
                foreach (var attr in list.Attributes)
                {
                    if (attr.ArgumentList is null) continue;
                    foreach (var arg in attr.ArgumentList.Arguments)
                    {
                        if (arg.NameEquals?.Name.Identifier.Text == "Compensate")
                        {
                            compensateName = ExtractNameOfArgument(arg.Expression)
                                ?? (arg.Expression is LiteralExpressionSyntax lit ? lit.Token.ValueText : null);
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(compensateName)) continue;

            // If a method with that name already exists, skip — the diagnostic will
            // be about a wrong shape, which our generator stub cannot safely rewrite.
            if (classDecl.Members.OfType<MethodDeclarationSyntax>()
                .Any(m => string.Equals(m.Identifier.Text, compensateName, System.StringComparison.Ordinal)))
            {
                continue;
            }

            var capturedName = compensateName!;
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: Title,
                    createChangedDocument: ct => AddStubAsync(context.Document, classDecl, stepMethod, capturedName, ct),
                    equivalenceKey: Title + ":" + capturedName),
                diagnostic);
        }
    }

    private static string? ExtractNameOfArgument(ExpressionSyntax expression)
    {
        if (expression is InvocationExpressionSyntax inv
            && inv.Expression is IdentifierNameSyntax id
            && id.Identifier.Text == "nameof"
            && inv.ArgumentList.Arguments.Count == 1)
        {
            var inner = inv.ArgumentList.Arguments[0].Expression;
            if (inner is IdentifierNameSyntax i) return i.Identifier.Text;
            if (inner is MemberAccessExpressionSyntax m) return m.Name.Identifier.Text;
        }
        return null;
    }

    private static async Task<Document> AddStubAsync(
        Document document,
        ClassDeclarationSyntax classDecl,
        MethodDeclarationSyntax stepMethod,
        string compensateName,
        CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return document;

        // Use the step's return type as a strong hint for the compensation method's type.
        var returnType = stepMethod.ReturnType.WithoutTrivia();

        var stubMethod = SyntaxFactory.MethodDeclaration(returnType, SyntaxFactory.Identifier(compensateName))
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList())
            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression, SyntaxFactory.Token(SyntaxKind.DefaultKeyword)))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithAdditionalAnnotations(Formatter.Annotation);

        var newClass = classDecl.AddMembers(stubMethod);
        var newRoot = root.ReplaceNode(classDecl, newClass);
        return document.WithSyntaxRoot(newRoot);
    }
}
