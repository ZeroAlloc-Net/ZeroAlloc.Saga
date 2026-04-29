using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

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
    public static SagaModel? From(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol) return null;
        ct.ThrowIfCancellationRequested();

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

            // [CorrelationKey] — must take exactly one parameter (the event), return the key.
            var corrAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "ZeroAlloc.Saga.CorrelationKeyAttribute");
            if (corrAttr is not null)
            {
                if (member.Parameters.Length != 1) continue;
                var keyType = member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var stripped = StripGlobalPrefix(keyType);
                correlationKeyType ??= stripped;
                if (!string.Equals(correlationKeyType, stripped, System.StringComparison.Ordinal))
                {
                    // Inconsistent correlation key types — skip this saga (PR 3 will diagnose).
                    return null;
                }
                var eventTypeFqn = StripGlobalPrefix(member.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                correlations.Add(new CorrelationInfo(member.Name, eventTypeFqn));
                continue;
            }

            // [Step] — must take exactly one parameter (the trigger event), return the command.
            var stepAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "ZeroAlloc.Saga.StepAttribute");
            if (stepAttr is not null)
            {
                if (member.Parameters.Length != 1) continue;

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
                steps.Add(new StepInfo(order, member.Name, eventTypeFqn, commandTypeFqn, compensateName, compensateOnFqn));
            }
        }

        if (correlationKeyType is null) return null;
        if (steps.Count == 0) return null;

        // Sort steps by Order to get a stable forward sequence.
        steps.Sort((a, b) => a.Order.CompareTo(b.Order));

        var compensateOn = steps
            .Where(s => s.CompensateOnEventTypeFqn is not null)
            .Select(s => s.CompensateOnEventTypeFqn!)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        return new SagaModel(ns, name, accessibility, correlationKeyType, steps, correlations, compensateOn);
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
    string? CompensateOnEventTypeFqn);

internal sealed record CorrelationInfo(
    string MethodName,
    string EventTypeFqn);
