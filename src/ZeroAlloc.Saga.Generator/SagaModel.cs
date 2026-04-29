using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZeroAlloc.Saga.Generator.Diagnostics;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Semantic model the generator extracts from a [Saga] class.
/// </summary>
internal sealed record SagaModel(
    string Namespace,
    string ClassName,
    string Accessibility,
    string CorrelationKeyTypeFqn,
    IReadOnlyList<StepInfo> Steps,
    IReadOnlyList<CorrelationInfo> Correlations,
    IReadOnlyList<string> CompensateOnEventFqns)
{
    /// <summary>
    /// Extracts the saga model and any authoring-time diagnostics. The model is
    /// returned non-null only when the saga shape is sufficiently valid that emit
    /// can still produce something meaningful; otherwise the diagnostics list
    /// describes why and the model is null. Diagnostics are reported by
    /// <see cref="SagaGenerator"/> via <c>SourceProductionContext.ReportDiagnostic</c>.
    /// </summary>
    public static SagaExtractResult From(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol)
        {
            return new SagaExtractResult(null, diagnostics.ToImmutable());
        }
        ct.ThrowIfCancellationRequested();

        var classDecl = ctx.TargetNode as ClassDeclarationSyntax;
        var classNameLocation = classDecl?.Identifier.GetLocation() ?? classSymbol.Locations.FirstOrDefault();

        // ── ZASAGA001: must be partial ──────────────────────────────────────
        var isPartial = classDecl?.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)) ?? false;
        if (!isPartial)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                SagaDiagnostics.SagaClassMustBePartial,
                classNameLocation,
                classSymbol.Name));
        }

        // ── ZASAGA002: not static / abstract / generic / nested ─────────────
        var disallowed = new List<string>();
        if (classSymbol.IsStatic) disallowed.Add("static");
        if (classSymbol.IsAbstract) disallowed.Add("abstract");
        if (classSymbol.IsGenericType) disallowed.Add("generic");
        if (classSymbol.ContainingType is not null) disallowed.Add("nested");
        if (disallowed.Count > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                SagaDiagnostics.SagaClassUnsupportedShape,
                classNameLocation,
                classSymbol.Name,
                string.Join("/", disallowed)));
        }

        // ── ZASAGA003: needs accessible parameterless ctor ──────────────────
        // A class with no explicit ctor declaration gets an implicit public one — accept that.
        // Otherwise look for a parameterless one with public/internal accessibility.
        if (!classSymbol.IsStatic && !classSymbol.IsAbstract)
        {
            var explicitCtors = classSymbol.InstanceConstructors
                .Where(c => !c.IsImplicitlyDeclared)
                .ToList();
            if (explicitCtors.Count > 0)
            {
                var hasAccessibleParameterless = explicitCtors.Any(c =>
                    c.Parameters.Length == 0 &&
                    (c.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public ||
                     c.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Internal));
                if (!hasAccessibleParameterless)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.SagaMissingParameterlessCtor,
                        classNameLocation,
                        classSymbol.Name));
                }
            }
        }

        var ns = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : classSymbol.ContainingNamespace.ToDisplayString();

        var name = classSymbol.Name;
        var accessibility = classSymbol.DeclaredAccessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            _ => "internal",
        };

        var steps = new List<StepInfo>();
        var correlations = new List<CorrelationInfo>();
        string? correlationKeyType = null;

        foreach (var member in classSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();

            // [CorrelationKey]
            var corrAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "ZeroAlloc.Saga.CorrelationKeyAttribute");
            if (corrAttr is not null)
            {
                var memberLoc = member.Locations.FirstOrDefault();

                // ZASAGA006: must be 'TKey M(TEvent e)'
                if (member.Parameters.Length != 1 || member.ReturnsVoid)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CorrelationKeyBadSignature,
                        memberLoc,
                        member.Name));
                    continue;
                }

                var keyType = StripGlobalPrefix(member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                if (correlationKeyType is null)
                {
                    correlationKeyType = keyType;
                }
                else if (!string.Equals(correlationKeyType, keyType, System.StringComparison.Ordinal))
                {
                    // ZASAGA005
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.InconsistentCorrelationKeyTypes,
                        memberLoc,
                        classSymbol.Name,
                        correlationKeyType,
                        keyType));
                    continue;
                }

                var eventTypeFqn = StripGlobalPrefix(member.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                correlations.Add(new CorrelationInfo(member.Name, eventTypeFqn));

                // ZASAGA011: heuristic — body looks like it mutates state.
                if (CorrelationKeyAppearsToMutate(member, ct))
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CorrelationKeyMutatesState,
                        memberLoc,
                        member.Name));
                }
                continue;
            }

            // [Step]
            var stepAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "ZeroAlloc.Saga.StepAttribute");
            if (stepAttr is not null)
            {
                var memberLoc = member.Locations.FirstOrDefault();

                // ZASAGA008: shape must be 'TCommand M(TEvent e)'.
                if (member.Parameters.Length != 1 || member.ReturnsVoid)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.StepBadSignature,
                        memberLoc,
                        member.Name));
                    continue;
                }

                int order = 0;
                string? compensateName = null;
                string? compensateOnFqn = null;
                foreach (var arg in stepAttr.NamedArguments)
                {
                    if (string.Equals(arg.Key, "Order", System.StringComparison.Ordinal) && arg.Value.Value is int o)
                        order = o;
                    else if (string.Equals(arg.Key, "Compensate", System.StringComparison.Ordinal) && arg.Value.Value is string s)
                        compensateName = s;
                    else if (string.Equals(arg.Key, "CompensateOn", System.StringComparison.Ordinal) && arg.Value.Value is INamedTypeSymbol t)
                        compensateOnFqn = StripGlobalPrefix(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }

                var eventTypeFqn = StripGlobalPrefix(member.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                var commandTypeFqn = StripGlobalPrefix(member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                steps.Add(new StepInfo(order, member.Name, eventTypeFqn, commandTypeFqn, compensateName, compensateOnFqn, memberLoc));
            }
        }

        // ── ZASAGA004: each step's input event must have a [CorrelationKey] ─
        var correlatedEvents = new HashSet<string>(correlations.Select(c => c.EventTypeFqn), System.StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!correlatedEvents.Contains(step.EventTypeFqn))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    SagaDiagnostics.StepEventLacksCorrelationKey,
                    step.Location,
                    step.MethodName,
                    step.EventTypeFqn));
            }
        }

        // ── ZASAGA007: step Order values must be 1..N contiguous ────────────
        if (steps.Count > 0)
        {
            var orders = steps.Select(s => s.Order).OrderBy(x => x).ToList();
            var expected = Enumerable.Range(1, steps.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    SagaDiagnostics.StepOrderGapsOrDuplicates,
                    classNameLocation,
                    classSymbol.Name,
                    string.Join(", ", orders)));
            }
        }

        // ── ZASAGA009 / ZASAGA010 / ZASAGA012 ───────────────────────────────
        var allMembers = classSymbol.GetMembers().OfType<IMethodSymbol>().ToList();
        foreach (var step in steps)
        {
            // ZASAGA009: Compensate target must exist and be parameterless, non-void.
            if (step.CompensateMethodName is not null)
            {
                var target = allMembers.FirstOrDefault(m => string.Equals(m.Name, step.CompensateMethodName, System.StringComparison.Ordinal));
                var ok = target is not null
                    && target.Parameters.Length == 0
                    && !target.ReturnsVoid;
                if (!ok)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CompensateMethodMissingOrBadShape,
                        step.Location,
                        step.MethodName,
                        step.CompensateMethodName));
                }
            }

            // ZASAGA010: CompensateOn event needs a CorrelationKey method.
            if (step.CompensateOnEventTypeFqn is not null
                && !correlatedEvents.Contains(step.CompensateOnEventTypeFqn))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    SagaDiagnostics.CompensateOnEventLacksCorrelationKey,
                    step.Location,
                    step.MethodName,
                    step.CompensateOnEventTypeFqn));
            }

            // ZASAGA012: Compensate without CompensateOn = dead code, *unless* a
            // later step in the saga has its own CompensateOn — the reverse cascade
            // will still drive this method as part of compensating prior steps.
            if (step.CompensateMethodName is not null && step.CompensateOnEventTypeFqn is null)
            {
                var hasLaterCompensateOn = steps.Any(s => s.Order > step.Order && s.CompensateOnEventTypeFqn is not null);
                if (!hasLaterCompensateOn)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CompensateWithoutCompensateOn,
                        step.Location,
                        step.MethodName,
                        step.CompensateMethodName));
                }
            }
        }

        // If we cannot determine a correlation key type or have no steps, no model.
        if (correlationKeyType is null || steps.Count == 0)
        {
            return new SagaExtractResult(null, diagnostics.ToImmutable());
        }

        // Sort steps by Order to get a stable forward sequence.
        steps.Sort((a, b) => a.Order.CompareTo(b.Order));

        var compensateOn = steps
            .Where(s => s.CompensateOnEventTypeFqn is not null)
            .Select(s => s.CompensateOnEventTypeFqn!)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        var model = new SagaModel(ns, name, accessibility, correlationKeyType, steps, correlations, compensateOn);
        return new SagaExtractResult(model, diagnostics.ToImmutable());
    }

    private static bool CorrelationKeyAppearsToMutate(IMethodSymbol method, CancellationToken ct)
    {
        // Heuristic syntactic check: scan the syntax tree of any declaration for
        // assignment expressions or compound-assignment operators inside the body.
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var node = declRef.GetSyntax(ct);
            if (node is null) continue;
            foreach (var d in node.DescendantNodes())
            {
                if (d is AssignmentExpressionSyntax) return true;
                if (d is PostfixUnaryExpressionSyntax pu &&
                    (pu.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PlusPlusToken) ||
                     pu.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MinusMinusToken)))
                    return true;
                if (d is PrefixUnaryExpressionSyntax pre &&
                    (pre.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PlusPlusToken) ||
                     pre.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MinusMinusToken)))
                    return true;
            }
        }
        return false;
    }

    private static string StripGlobalPrefix(string s)
        => s.StartsWith("global::", System.StringComparison.Ordinal) ? s.Substring("global::".Length) : s;
}

internal sealed record StepInfo(
    int Order,
    string MethodName,
    string EventTypeFqn,
    string CommandTypeFqn,
    string? CompensateMethodName,
    string? CompensateOnEventTypeFqn,
    Location? Location = null);

internal sealed record CorrelationInfo(
    string MethodName,
    string EventTypeFqn);

/// <summary>
/// Carries the optional model plus any diagnostics surfaced during extraction.
/// </summary>
internal sealed record SagaExtractResult(
    SagaModel? Model,
    ImmutableArray<DiagnosticInfo> Diagnostics);
