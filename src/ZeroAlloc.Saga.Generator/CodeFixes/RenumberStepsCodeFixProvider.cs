using System.Collections.Generic;
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
/// Code-fix provider for <c>ZASAGA007</c> — renumbers <c>[Step(Order = N)]</c>
/// values across the saga so they form the contiguous sequence 1, 2, 3, ...
/// preserving the relative order the user already gave.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RenumberStepsCodeFixProvider))]
[Shared]
public sealed class RenumberStepsCodeFixProvider : CodeFixProvider
{
    private const string DiagnosticId = "ZASAGA007";
    private const string Title = "Renumber [Step(Order = ...)] to 1..N";

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
                    createChangedDocument: ct => RenumberAsync(context.Document, classDecl, ct),
                    equivalenceKey: Title),
                diagnostic);
        }
    }

    private static async Task<Document> RenumberAsync(Document document, ClassDeclarationSyntax classDecl, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        if (root is null) return document;

        // Pair every [Step] attribute with the (parsed) order it currently has, then
        // sort by that to derive the renumber map.
        var stepAttrs = new List<(AttributeSyntax Attr, int CurrentOrder)>();
        foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            foreach (var list in method.AttributeLists)
            {
                foreach (var attr in list.Attributes)
                {
                    var name = attr.Name.ToString();
                    if (name.EndsWith("Step", System.StringComparison.Ordinal)
                        || name.EndsWith("StepAttribute", System.StringComparison.Ordinal))
                    {
                        stepAttrs.Add((attr, ExtractOrder(attr)));
                    }
                }
            }
        }

        // Sort by current Order to preserve relative author ordering.
        var ordered = stepAttrs
            .OrderBy(t => t.CurrentOrder)
            .Select((t, idx) => (t.Attr, NewOrder: idx + 1))
            .ToList();

        var newRoot = root.ReplaceNodes(
            ordered.Select(t => (SyntaxNode)t.Attr),
            (original, _) =>
            {
                var match = ordered.First(o => o.Attr == original);
                return WithOrder((AttributeSyntax)original, match.NewOrder);
            });

        return document.WithSyntaxRoot(newRoot);
    }

    private static int ExtractOrder(AttributeSyntax attr)
    {
        if (attr.ArgumentList is null) return int.MaxValue;
        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals?.Name.Identifier.Text == "Order"
                && arg.Expression is LiteralExpressionSyntax lit
                && lit.Token.Value is int v)
            {
                return v;
            }
        }
        return int.MaxValue;
    }

    private static AttributeSyntax WithOrder(AttributeSyntax attr, int newOrder)
    {
        var newLiteral = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(newOrder));

        if (attr.ArgumentList is null)
        {
            var argList = SyntaxFactory.AttributeArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.AttributeArgument(newLiteral)
                        .WithNameEquals(SyntaxFactory.NameEquals("Order"))));
            return attr.WithArgumentList(argList);
        }

        var args = attr.ArgumentList.Arguments;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].NameEquals?.Name.Identifier.Text == "Order")
            {
                var replaced = args[i].WithExpression(newLiteral);
                var newArgs = args.Replace(args[i], replaced);
                return attr.WithArgumentList(attr.ArgumentList.WithArguments(newArgs));
            }
        }

        // No existing Order arg — prepend.
        var prepended = args.Insert(0,
            SyntaxFactory.AttributeArgument(newLiteral)
                .WithNameEquals(SyntaxFactory.NameEquals("Order")));
        return attr.WithArgumentList(attr.ArgumentList.WithArguments(prepended));
    }
}
