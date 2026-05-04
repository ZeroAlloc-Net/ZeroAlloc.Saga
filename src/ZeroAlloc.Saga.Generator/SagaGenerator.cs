using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZeroAlloc.Saga.Generator.Diagnostics;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Source generator for [Saga] partial classes. For each saga, emits:
///   1. {SagaName}Fsm.g.cs            — inline FSM partial (state, triggers, TryFire)
///   2. {SagaName}.g.cs               — partial-class completion attaching the Fsm property
///   3. {SagaName}_{Event}_Handler.g.cs — one INotificationHandler per event
///   4. {SagaName}CorrelationDispatch.g.cs — typed event-to-key dispatch
///   5. {SagaName}BuilderExtensions.g.cs — AOT-safe DI registrations + compensation dispatcher
///
/// The generator also reports authoring diagnostics ZASAGA001-013 directly via
/// <see cref="SourceProductionContext.ReportDiagnostic"/>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SagaGenerator : IIncrementalGenerator
{
    private const string SagaAttributeFqn = "ZeroAlloc.Saga.SagaAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var extracted = context.SyntaxProvider.ForAttributeWithMetadataName(
            SagaAttributeFqn,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) => SagaModel.From(ctx, ct));

        // Per-saga emission + per-saga diagnostic reporting.
        context.RegisterSourceOutput(extracted, static (spc, result) =>
        {
            foreach (var d in result.Diagnostics)
            {
                spc.ReportDiagnostic(d.ToDiagnostic());
            }

            if (result.Model is not null)
            {
                FsmEmitter.Emit(spc, result.Model);
                PartialCompletionEmitter.Emit(spc, result.Model);
                HandlerEmitter.Emit(spc, result.Model);
                CorrelationDispatchEmitter.Emit(spc, result.Model);
                BuilderExtensionsEmitter.Emit(spc, result.Model);
                SnapshotRestoreEmitter.Emit(spc, result.Model);
            }
        });

        // Cross-saga ZASAGA013: two sagas correlate on the same event but with different key types.
        var allModels = extracted.Collect();
        context.RegisterSourceOutput(allModels, static (spc, results) =>
        {
            ReportCrossSagaDiagnostics(spc, results);
        });

        // Per-compilation MediatorSagaCommandDispatcher — single emit covering every
        // [Step] command type across all sagas in the consumer assembly. Lives in the
        // consumer's compilation so it can reference IMediator directly (which is
        // emitted per-assembly by the Mediator source generator).
        context.RegisterSourceOutput(allModels, static (spc, results) =>
        {
            MediatorSagaCommandDispatcherEmitter.Emit(spc, results);
        });

        // ZASAGA015: best-effort idempotency hint when a durable backend is wired
        // anywhere in the same compilation. We don't bind the call — we look for
        // any invocation whose name starts with WithEfCoreStore / WithRedisStore,
        // and emit one diagnostic per [Saga] in the compilation. Suppressible
        // via #pragma warning disable ZASAGA015.
        var hasDurableBackend = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsDurableBackendInvocation(node),
                transform: static (ctx, _) => true)
            .Collect()
            .Select(static (arr, _) => arr.Length > 0);

        var sagasAndBackend = allModels.Combine(hasDurableBackend);
        context.RegisterSourceOutput(sagasAndBackend, static (spc, tuple) =>
        {
            var (results, hasBackend) = tuple;
            if (!hasBackend) return;
            foreach (var result in results)
            {
                if (result.Model is null) continue;
                spc.ReportDiagnostic(Diagnostic.Create(
                    SagaDiagnostics.IdempotencyHint,
                    location: null,
                    result.Model.ClassName));
            }
        });
    }

    private static bool IsDurableBackendInvocation(SyntaxNode node)
    {
        if (node is not Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax inv) return false;
        var name = inv.Expression switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.ValueText,
            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax idn => idn.Identifier.ValueText,
            _ => null,
        };
        return name is "WithEfCoreStore" or "WithRedisStore";
    }

    private static void ReportCrossSagaDiagnostics(SourceProductionContext spc, ImmutableArray<SagaExtractResult> results)
    {
        // For every event-type observed by a saga's correlation methods, gather
        // (saga name, key type). If two sagas observe the same event with
        // different key types, report ZASAGA013 once.
        var byEvent = new Dictionary<string, List<(string Saga, string KeyType, Location? Loc)>>(System.StringComparer.Ordinal);
        foreach (var result in results)
        {
            var model = result.Model;
            if (model is null) continue;
            foreach (var corr in model.Correlations)
            {
                if (!byEvent.TryGetValue(corr.EventTypeFqn, out var list))
                {
                    list = new List<(string, string, Location?)>();
                    byEvent[corr.EventTypeFqn] = list;
                }
                list.Add((model.ClassName, model.CorrelationKeyTypeFqn, null));
            }
        }

        foreach (var kvp in byEvent)
        {
            var entries = kvp.Value;
            if (entries.Count < 2) continue;
            for (int i = 0; i < entries.Count; i++)
            {
                for (int j = i + 1; j < entries.Count; j++)
                {
                    if (!string.Equals(entries[i].KeyType, entries[j].KeyType, System.StringComparison.Ordinal))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            SagaDiagnostics.DuplicateSagaCorrelationKeyType,
                            location: null,
                            entries[i].Saga,
                            entries[j].Saga,
                            kvp.Key,
                            entries[i].KeyType,
                            entries[j].KeyType));
                    }
                }
            }
        }
    }
}
