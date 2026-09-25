using Microsoft.CodeAnalysis;

namespace ZeroAlloc.Saga.Generator.Diagnostics;

/// <summary>
/// Diagnostic descriptors for <c>ZASAGA001</c>-<c>ZASAGA017</c>. These are reported
/// directly by <see cref="SagaGenerator"/> (and its emitters) via
/// <c>SourceProductionContext.ReportDiagnostic</c> when a user's <c>[Saga]</c> shape
/// violates the authoring contract.
/// </summary>
internal static class SagaDiagnostics
{
    // Every descriptor is a direct constructor call with its ID, category and severity as
    // constants. The Roslyn release-tracking analyzers read those arguments at the call
    // site; behind a shared factory they saw only the factory's parameters, so a change to
    // a shipped rule's severity or category was not flagged. See ZeroAlloc-Net/.github#38.
    private const string Category = "ZeroAlloc.Saga.Authoring";
    private const string HelpLinkBase = "https://github.com/ZeroAlloc-Net/ZeroAlloc.Saga/blob/main/docs/diagnostics.md#";

    public static readonly DiagnosticDescriptor SagaClassMustBePartial = new DiagnosticDescriptor(
        id: "ZASAGA001",
        title: "[Saga] class must be partial",
        messageFormat: "Saga class '{0}' must be declared 'partial' so the generator can extend it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "ZeroAlloc.Saga emits an FSM partial onto every [Saga] class. The class must be marked 'partial' for the generator output to compile.",
        helpLinkUri: HelpLinkBase + "zasaga001");

    public static readonly DiagnosticDescriptor SagaClassUnsupportedShape = new DiagnosticDescriptor(
        id: "ZASAGA002",
        title: "[Saga] class has unsupported shape",
        messageFormat: "Saga class '{0}' is {1}; sagas must be non-static, non-abstract, non-generic, top-level types",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Sagas are instantiated via a parameterless constructor and registered as top-level types. Static, abstract, generic, or nested classes are not supported.",
        helpLinkUri: HelpLinkBase + "zasaga002");

    public static readonly DiagnosticDescriptor SagaMissingParameterlessCtor = new DiagnosticDescriptor(
        id: "ZASAGA003",
        title: "[Saga] class lacks an accessible parameterless constructor",
        messageFormat: "Saga class '{0}' must expose an accessible parameterless constructor",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "ZeroAlloc.Saga creates new saga instances via 'new T()' (AOT-safe, no reflection). Add a public or internal parameterless constructor.",
        helpLinkUri: HelpLinkBase + "zasaga003");

    public static readonly DiagnosticDescriptor StepEventLacksCorrelationKey = new DiagnosticDescriptor(
        id: "ZASAGA004",
        title: "[Step] input event has no [CorrelationKey] method",
        messageFormat: "Step '{0}' takes event '{1}' but no [CorrelationKey] method maps that event to a correlation key",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every step's input event type must be mapped to the saga's correlation key by a [CorrelationKey] method on the same saga class.",
        helpLinkUri: HelpLinkBase + "zasaga004");

    public static readonly DiagnosticDescriptor InconsistentCorrelationKeyTypes = new DiagnosticDescriptor(
        id: "ZASAGA005",
        title: "[CorrelationKey] methods return inconsistent types",
        messageFormat: "Saga '{0}' has [CorrelationKey] methods returning different types ('{1}' vs '{2}'); all correlation keys must share one type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A saga has exactly one correlation key type. All [CorrelationKey] methods within the saga must return the same type.",
        helpLinkUri: HelpLinkBase + "zasaga005");

    public static readonly DiagnosticDescriptor CorrelationKeyBadSignature = new DiagnosticDescriptor(
        id: "ZASAGA006",
        title: "[CorrelationKey] method has wrong signature",
        messageFormat: "[CorrelationKey] method '{0}' must take exactly one event parameter and return the correlation key",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A [CorrelationKey] method has the shape 'TKey M(TEvent e)'. Other shapes (no parameters, multiple parameters, void return) are not supported.",
        helpLinkUri: HelpLinkBase + "zasaga006");

    public static readonly DiagnosticDescriptor StepOrderGapsOrDuplicates = new DiagnosticDescriptor(
        id: "ZASAGA007",
        title: "[Step(Order = ...)] values have gaps or duplicates",
        messageFormat: "Saga '{0}' has [Step] Order values that are not contiguous starting at 1: [{1}]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Step Order values must form the sequence 1, 2, 3, ... with no gaps and no duplicates so the generated FSM has a deterministic forward path.",
        helpLinkUri: HelpLinkBase + "zasaga007");

    public static readonly DiagnosticDescriptor StepBadSignature = new DiagnosticDescriptor(
        id: "ZASAGA008",
        title: "[Step] method has wrong signature",
        messageFormat: "[Step] method '{0}' must take exactly one event parameter and return a command type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A [Step] method has the shape 'TCommand M(TEvent e)'. Other shapes are not supported.",
        helpLinkUri: HelpLinkBase + "zasaga008");

    public static readonly DiagnosticDescriptor CompensateMethodMissingOrBadShape = new DiagnosticDescriptor(
        id: "ZASAGA009",
        title: "[Step.Compensate] target is missing or mis-shaped",
        messageFormat: "Step '{0}' references compensation method '{1}' which does not exist or has the wrong shape (expected: parameterless method returning a command)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "[Step.Compensate = nameof(X)] must point at a parameterless method on the saga that returns the compensation command.",
        helpLinkUri: HelpLinkBase + "zasaga009");

    public static readonly DiagnosticDescriptor CompensateOnEventLacksCorrelationKey = new DiagnosticDescriptor(
        id: "ZASAGA010",
        title: "[Step.CompensateOn] event has no [CorrelationKey]",
        messageFormat: "Step '{0}' compensates on event '{1}' but no [CorrelationKey] method maps that event to a correlation key",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "When a step uses [Step.CompensateOn = typeof(X)], event 'X' must also be wired with a [CorrelationKey] method so the dispatcher can locate the saga instance.",
        helpLinkUri: HelpLinkBase + "zasaga010");

    public static readonly DiagnosticDescriptor CorrelationKeyMutatesState = new DiagnosticDescriptor(
        id: "ZASAGA011",
        title: "[CorrelationKey] method appears to mutate state",
        messageFormat: "[CorrelationKey] method '{0}' appears to mutate state; correlation key extraction should be a pure read of the event",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Correlation key extraction may run before the saga instance is loaded. Mutating fields, calling state setters, or invoking non-pure operations will give surprising behaviour.",
        helpLinkUri: HelpLinkBase + "zasaga011");

    public static readonly DiagnosticDescriptor CompensateWithoutCompensateOn = new DiagnosticDescriptor(
        id: "ZASAGA012",
        title: "Step has Compensate but no CompensateOn — dead code",
        messageFormat: "Step '{0}' declares Compensate = '{1}' but no CompensateOn; the compensation method will never run automatically",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Without a CompensateOn event, the generator does not wire the compensation method into any handler, so the method is unreachable through normal saga flow.",
        helpLinkUri: HelpLinkBase + "zasaga012");

    public static readonly DiagnosticDescriptor DuplicateSagaCorrelationKeyType = new DiagnosticDescriptor(
        id: "ZASAGA013",
        title: "Two [Saga] classes correlate on same event with different key types",
        messageFormat: "Sagas '{0}' and '{1}' both correlate on event '{2}' but use different correlation key types ('{3}' vs '{4}')",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Multiple sagas may legitimately observe the same event, but using different correlation key types for the same event makes the dispatch boundary ambiguous.",
        helpLinkUri: HelpLinkBase + "zasaga013");

    public static readonly DiagnosticDescriptor UnsupportedFieldType = new DiagnosticDescriptor(
        id: "ZASAGA014",
        title: "Saga state field has an unsupported type",
        messageFormat: "Field '{0}' on saga '{1}' has type '{2}' which is not supported by the v1.1 byte serializer. Supported types: primitives, enums, string, DateTime/DateTimeOffset/TimeSpan/Guid, [TypedId] types, byte[], and Nullable<T> of supported types. Mark with [NotSagaState] to exclude from persistence, or wait for the v1.x extension point (BACKLOG #18).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The v1.1 saga byte serializer covers the common-case state shapes — primitives, enums, well-known structs, [TypedId]s, byte[], and nullable wrappers thereof. Other shapes (collections, custom records, polymorphism) are deferred to a later extension point.",
        helpLinkUri: HelpLinkBase + "zasaga014");

    public static readonly DiagnosticDescriptor IdempotencyHint = new DiagnosticDescriptor(
        id: "ZASAGA015",
        title: "Saga commands should be idempotent under durable backends",
        messageFormat: "Saga '{0}' uses a durable backend (WithEfCoreStore/WithRedisStore). Commands dispatched by [Step] methods should be idempotent — under OCC retry-after-conflict semantics, a step's command may be dispatched twice. Suppress with `#pragma warning disable ZASAGA015` if intentional, or use Saga.Outbox bridge (Phase 3) for at-least-once delivery without double-dispatch.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Durable saga backends use optimistic concurrency control (OCC). Under contention the entire notification handler — including the user's [Step] method and its emitted command — is retried. Idempotent commands tolerate that; non-idempotent ones may double-charge, double-ship, etc.",
        helpLinkUri: HelpLinkBase + "zasaga015");

    public static readonly DiagnosticDescriptor StepCommandTypeNotPartial = new DiagnosticDescriptor(
        id: "ZASAGA016",
        title: "Step command type must be partial when ZeroAlloc.Serialisation is referenced",
        messageFormat: "Step command type '{0}' must be 'partial' so the Saga generator can apply [ZeroAllocSerializable] via partial-class extension. Add the 'partial' modifier.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "When ZeroAlloc.Serialisation is referenced the Saga generator extends each step's command type with a partial declaration carrying [ZeroAllocSerializable]. The command type must therefore be declared 'partial' so the generated partial can attach.",
        helpLinkUri: HelpLinkBase + "zasaga016");

    public static readonly DiagnosticDescriptor StepCommandTypeCrossAssembly = new DiagnosticDescriptor(
        id: "ZASAGA017",
        title: "Step command type is in a referenced assembly",
        messageFormat: "Step command type '{0}' is declared in a referenced assembly. The Saga generator cannot apply [ZeroAllocSerializable] via partial-class extension on foreign types. Apply [ZeroAllocSerializable] manually on the type's declaration.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Partial-class extension can only attach to types declared in the same compilation. For step command types declared in a referenced assembly, apply [ZeroAllocSerializable] manually at the type's source declaration site.",
        helpLinkUri: HelpLinkBase + "zasaga017");
}
