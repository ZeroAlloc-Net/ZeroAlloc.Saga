; Unshipped analyzer release.
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
ZASAGA014 | ZeroAlloc.Saga.Authoring | Error | Saga state field has an unsupported type
ZASAGA015 | ZeroAlloc.Saga.Authoring | Info | Saga commands should be idempotent under durable backends
ZASAGA016 | ZeroAlloc.Saga.Authoring | Warning | Step command type must be partial when ZeroAlloc.Serialisation is referenced
ZASAGA017 | ZeroAlloc.Saga.Authoring | Info | Step command type is in a referenced assembly
