using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Saga.Generator.Diagnostics;

/// <summary>
/// Diagnostic descriptors for <c>ZASAGA001</c>-<c>ZASAGA013</c>. These are reported
/// directly by <see cref="SagaGenerator"/> (and its emitters) via
/// <c>SourceProductionContext.ReportDiagnostic</c> when a user's <c>[Saga]</c> shape
/// violates the authoring contract.
/// </summary>
internal static class SagaDiagnostics
{
    private const string Category = "ZeroAlloc.Saga.Authoring";
    private const string HelpLinkBase = "https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/diagnostics.md#";

    private static DiagnosticDescriptor Make(
        string id,
        string title,
        string messageFormat,
        DiagnosticSeverity severity,
        string description) => new(
            id: id,
            title: title,
            messageFormat: messageFormat,
            category: Category,
            defaultSeverity: severity,
            isEnabledByDefault: true,
            description: description,
            helpLinkUri: HelpLinkBase + id.ToLowerInvariant());

    public static readonly DiagnosticDescriptor SagaClassMustBePartial = Make(
        id: "ZASAGA001",
        title: "[Saga] class must be partial",
        messageFormat: "Saga class '{0}' must be declared 'partial' so the generator can extend it",
        severity: DiagnosticSeverity.Error,
        description: "ZeroAlloc.Saga emits an FSM partial onto every [Saga] class. The class must be marked 'partial' for the generator output to compile.");

    public static readonly DiagnosticDescriptor SagaClassUnsupportedShape = Make(
        id: "ZASAGA002",
        title: "[Saga] class has unsupported shape",
        messageFormat: "Saga class '{0}' is {1}; sagas must be non-static, non-abstract, non-generic, top-level types",
        severity: DiagnosticSeverity.Error,
        description: "Sagas are instantiated via a parameterless constructor and registered as top-level types. Static, abstract, generic, or nested classes are not supported.");

    public static readonly DiagnosticDescriptor SagaMissingParameterlessCtor = Make(
        id: "ZASAGA003",
        title: "[Saga] class lacks an accessible parameterless constructor",
        messageFormat: "Saga class '{0}' must expose an accessible parameterless constructor",
        severity: DiagnosticSeverity.Error,
        description: "ZeroAlloc.Saga creates new saga instances via 'new T()' (AOT-safe, no reflection). Add a public or internal parameterless constructor.");

    public static readonly DiagnosticDescriptor StepEventLacksCorrelationKey = Make(
        id: "ZASAGA004",
        title: "[Step] input event has no [CorrelationKey] method",
        messageFormat: "Step '{0}' takes event '{1}' but no [CorrelationKey] method maps that event to a correlation key",
        severity: DiagnosticSeverity.Error,
        description: "Every step's input event type must be mapped to the saga's correlation key by a [CorrelationKey] method on the same saga class.");

    public static readonly DiagnosticDescriptor InconsistentCorrelationKeyTypes = Make(
        id: "ZASAGA005",
        title: "[CorrelationKey] methods return inconsistent types",
        messageFormat: "Saga '{0}' has [CorrelationKey] methods returning different types ('{1}' vs '{2}'); all correlation keys must share one type",
        severity: DiagnosticSeverity.Error,
        description: "A saga has exactly one correlation key type. All [CorrelationKey] methods within the saga must return the same type.");

    public static readonly DiagnosticDescriptor CorrelationKeyBadSignature = Make(
        id: "ZASAGA006",
        title: "[CorrelationKey] method has wrong signature",
        messageFormat: "[CorrelationKey] method '{0}' must take exactly one event parameter and return the correlation key",
        severity: DiagnosticSeverity.Error,
        description: "A [CorrelationKey] method has the shape 'TKey M(TEvent e)'. Other shapes (no parameters, multiple parameters, void return) are not supported.");

    public static readonly DiagnosticDescriptor StepOrderGapsOrDuplicates = Make(
        id: "ZASAGA007",
        title: "[Step(Order = ...)] values have gaps or duplicates",
        messageFormat: "Saga '{0}' has [Step] Order values that are not contiguous starting at 1: [{1}]",
        severity: DiagnosticSeverity.Error,
        description: "Step Order values must form the sequence 1, 2, 3, ... with no gaps and no duplicates so the generated FSM has a deterministic forward path.");

    public static readonly DiagnosticDescriptor StepBadSignature = Make(
        id: "ZASAGA008",
        title: "[Step] method has wrong signature",
        messageFormat: "[Step] method '{0}' must take exactly one event parameter and return a command type",
        severity: DiagnosticSeverity.Error,
        description: "A [Step] method has the shape 'TCommand M(TEvent e)'. Other shapes are not supported.");

    public static readonly DiagnosticDescriptor CompensateMethodMissingOrBadShape = Make(
        id: "ZASAGA009",
        title: "[Step.Compensate] target is missing or mis-shaped",
        messageFormat: "Step '{0}' references compensation method '{1}' which does not exist or has the wrong shape (expected: parameterless method returning a command)",
        severity: DiagnosticSeverity.Error,
        description: "[Step.Compensate = nameof(X)] must point at a parameterless method on the saga that returns the compensation command.");

    public static readonly DiagnosticDescriptor CompensateOnEventLacksCorrelationKey = Make(
        id: "ZASAGA010",
        title: "[Step.CompensateOn] event has no [CorrelationKey]",
        messageFormat: "Step '{0}' compensates on event '{1}' but no [CorrelationKey] method maps that event to a correlation key",
        severity: DiagnosticSeverity.Error,
        description: "When a step uses [Step.CompensateOn = typeof(X)], event 'X' must also be wired with a [CorrelationKey] method so the dispatcher can locate the saga instance.");

    public static readonly DiagnosticDescriptor CorrelationKeyMutatesState = Make(
        id: "ZASAGA011",
        title: "[CorrelationKey] method appears to mutate state",
        messageFormat: "[CorrelationKey] method '{0}' appears to mutate state; correlation key extraction should be a pure read of the event",
        severity: DiagnosticSeverity.Warning,
        description: "Correlation key extraction may run before the saga instance is loaded. Mutating fields, calling state setters, or invoking non-pure operations will give surprising behaviour.");

    public static readonly DiagnosticDescriptor CompensateWithoutCompensateOn = Make(
        id: "ZASAGA012",
        title: "Step has Compensate but no CompensateOn — dead code",
        messageFormat: "Step '{0}' declares Compensate = '{1}' but no CompensateOn; the compensation method will never run automatically",
        severity: DiagnosticSeverity.Warning,
        description: "Without a CompensateOn event, the generator does not wire the compensation method into any handler, so the method is unreachable through normal saga flow.");

    public static readonly DiagnosticDescriptor DuplicateSagaCorrelationKeyType = Make(
        id: "ZASAGA013",
        title: "Two [Saga] classes correlate on same event with different key types",
        messageFormat: "Sagas '{0}' and '{1}' both correlate on event '{2}' but use different correlation key types ('{3}' vs '{4}')",
        severity: DiagnosticSeverity.Warning,
        description: "Multiple sagas may legitimately observe the same event, but using different correlation key types for the same event makes the dispatch boundary ambiguous.");
}
