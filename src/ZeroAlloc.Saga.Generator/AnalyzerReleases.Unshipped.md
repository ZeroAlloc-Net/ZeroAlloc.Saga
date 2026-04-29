; Unshipped analyzer release.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ZASAGA001 | ZeroAlloc.Saga.Authoring | Error | [Saga] class must be partial
ZASAGA002 | ZeroAlloc.Saga.Authoring | Error | [Saga] class has unsupported shape
ZASAGA003 | ZeroAlloc.Saga.Authoring | Error | [Saga] class lacks accessible parameterless ctor
ZASAGA004 | ZeroAlloc.Saga.Authoring | Error | [Step] input event has no [CorrelationKey]
ZASAGA005 | ZeroAlloc.Saga.Authoring | Error | [CorrelationKey] methods return inconsistent types
ZASAGA006 | ZeroAlloc.Saga.Authoring | Error | [CorrelationKey] method has wrong signature
ZASAGA007 | ZeroAlloc.Saga.Authoring | Error | [Step(Order = ...)] values have gaps or duplicates
ZASAGA008 | ZeroAlloc.Saga.Authoring | Error | [Step] method has wrong signature
ZASAGA009 | ZeroAlloc.Saga.Authoring | Error | [Step.Compensate] target missing or mis-shaped
ZASAGA010 | ZeroAlloc.Saga.Authoring | Error | [Step.CompensateOn] event has no [CorrelationKey]
ZASAGA011 | ZeroAlloc.Saga.Authoring | Warning | [CorrelationKey] method appears to mutate state
ZASAGA012 | ZeroAlloc.Saga.Authoring | Warning | Step has Compensate but no CompensateOn
ZASAGA013 | ZeroAlloc.Saga.Authoring | Warning | Two sagas correlate on same event with different key types
