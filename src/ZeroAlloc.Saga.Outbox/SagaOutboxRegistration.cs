using System.Collections.Generic;

namespace ZeroAlloc.Saga.Outbox;

/// <summary>
/// What <see cref="SagaOutboxBuilderExtensions.WithOutbox"/> registered, for
/// <see cref="SagaOutboxStartupCheck"/>: the saga command type names it registered a dispatcher
/// for, and whether it found the generator-emitted registry at all. Its registration also marks
/// <c>WithOutbox()</c> as applied, so a second call registers nothing twice.
/// </summary>
internal sealed class SagaOutboxRegistration
{
    public SagaOutboxRegistration(IReadOnlyList<string> typeNames, bool registryFound)
    {
        TypeNames = typeNames;
        RegistryFound = registryFound;
    }

    /// <summary>The saga command type names, as <c>Type.FullName</c>.</summary>
    public IReadOnlyList<string> TypeNames { get; }

    /// <summary>False when no generator-emitted <c>SagaCommandRegistry</c> was found.</summary>
    public bool RegistryFound { get; }
}
