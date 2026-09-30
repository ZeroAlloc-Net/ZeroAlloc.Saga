#nullable enable
using System;
using System.Collections.Generic;
using ZeroAlloc.Saga.Generator.Diagnostics;
using System.Linq;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// The distinct saga command types of a compilation: every step's forward command and, where it
/// has one, its compensation command, ordered by name. The per-compilation dispatcher, registry
/// and command source all cover exactly this set.
/// </summary>
internal static class SagaCommandTypes
{
    public static List<SagaCommandType> Collect(EquatableArray<SagaExtractResult> sagaResults)
        => sagaResults
            .Select(r => r.Model)
            .Where(m => m is not null)
            .SelectMany(m => m!.Steps.SelectMany(st =>
            {
                // Saga handlers route compensations through the same ISagaCommandDispatcher,
                // so a compensation command needs a dispatch arm and a registry entry too.
                var step = new SagaCommandType(st.CommandTypeFqn, st.CommandTypeIsReferenceType);
                if (st.CompensateCommandTypeFqn is not null)
                    return new[] { step, new SagaCommandType(st.CompensateCommandTypeFqn, st.CompensateCommandTypeIsReferenceType) };
                return new[] { step };
            }))
            // A type name identifies the type, so every entry with the same name agrees on
            // IsReferenceType and keeping the first one loses nothing.
            .GroupBy(t => t.Fqn, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(t => t.Fqn, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Each saga's own distinct step and compensation command types, ordered by name, with the
    /// sagas ordered by name too.
    /// </summary>
    public static List<SagaCommandTypesOfSaga> CollectPerSaga(EquatableArray<SagaExtractResult> sagaResults)
        => sagaResults
            .Select(r => r.Model)
            .Where(m => m is not null)
            .Select(m => new SagaCommandTypesOfSaga(
                string.IsNullOrEmpty(m!.Namespace) ? m.ClassName : m.Namespace + "." + m.ClassName,
                m.Steps
                    .SelectMany(st => st.CompensateCommandTypeFqn is null
                        ? new[] { st.CommandTypeFqn }
                        : new[] { st.CommandTypeFqn, st.CompensateCommandTypeFqn })
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList()))
            .GroupBy(s => s.SagaFqn, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => s.SagaFqn, StringComparer.Ordinal)
            .ToList();
}

/// <summary>One saga and its own step and compensation command types.</summary>
/// <param name="SagaFqn">The saga's fully qualified name, without the <c>global::</c> prefix.</param>
/// <param name="CommandTypeFqns">Its distinct command type names, ordered by name.</param>
internal sealed record SagaCommandTypesOfSaga(string SagaFqn, List<string> CommandTypeFqns);

/// <summary>A saga command type, by fully qualified name without the <c>global::</c> prefix.</summary>
/// <param name="Fqn">The fully qualified type name.</param>
/// <param name="IsReferenceType">
/// True for a reference-type command, such as a record class. Only such a command can be null, so only it
/// needs a null check; comparing a struct command to null does not compile.
/// </param>
internal sealed record SagaCommandType(string Fqn, bool IsReferenceType);
