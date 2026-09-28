using System.Runtime.CompilerServices;

// SagaOutboxStartupCheck and SagaOutboxRegistration are internal; the test project constructs
// them directly to exercise start-check branches, such as a saga assembly without
// ZeroAlloc.Serialisation, that its own generator-emitted source cannot produce.
[assembly: InternalsVisibleTo("ZeroAlloc.Saga.Outbox.Tests")]
