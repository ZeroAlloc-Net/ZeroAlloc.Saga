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
    public static List<string> Collect(EquatableArray<SagaExtractResult> sagaResults)
        => sagaResults
            .Select(r => r.Model)
            .Where(m => m is not null)
            .SelectMany(m => m!.Steps.SelectMany(st =>
            {
                // Saga handlers route compensations through the same ISagaCommandDispatcher,
                // so a compensation command needs a dispatch arm and a registry entry too.
                if (st.CompensateCommandTypeFqn is not null)
                    return new[] { st.CommandTypeFqn, st.CompensateCommandTypeFqn };
                return new[] { st.CommandTypeFqn };
            }))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
}
