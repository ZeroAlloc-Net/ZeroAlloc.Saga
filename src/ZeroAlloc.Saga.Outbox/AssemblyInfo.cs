using System.Runtime.CompilerServices;

// SagaOutboxStartupCheck and SagaOutboxRegistration are internal; the test project constructs
// them directly to exercise the registry-not-found branch without needing a real generator-emitted
// registry to be absent from the whole test assembly.
[assembly: InternalsVisibleTo("ZeroAlloc.Saga.Outbox.Tests")]
