using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZeroAlloc.Saga.Generator.Diagnostics;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Source generator for [Saga] partial classes. For each saga, emits:
///   1. {Saga}.Fsm.g.cs                 — inline FSM partial (state, triggers, TryFire)
///   2. {Saga}.g.cs                     — partial-class completion attaching the Fsm property
///   3. {Saga}.Handler.{Event}.g.cs     — one INotificationHandler per event
///   4. {Saga}.CorrelationDispatch.g.cs — typed event-to-key dispatch
///   5. {Saga}.BuilderExtensions.g.cs   — AOT-safe DI registrations + compensation dispatcher
///   6. {Saga}.PersistableState.g.cs    — Snapshot/Restore of the saga's state
///
/// {Saga} and {Event} are the saga's and the event's names qualified by namespace, containing
/// types and generic arity; see <see cref="HintNames"/>.
///
/// and once per compilation MediatorSagaCommandDispatcher.g.cs, GeneratedSagaCommandSource.g.cs
/// and, when ZeroAlloc.Serialisation is referenced, SagaCommandRegistry.g.cs.
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
            transform: static (ctx, ct) => SagaModel.From(ctx, ct))
            .WithTrackingName(TrackingNames.SagaModels);

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
        var allModels = extracted.Collect()
            .Select(static (results, _) => new EquatableArray<SagaExtractResult>(results))
            .WithTrackingName(TrackingNames.AllSagaModels);
        context.RegisterSourceOutput(allModels, static (spc, results) =>
        {
            ReportCrossSagaDiagnostics(spc, results);
        });

        // Per-compilation SagaCommandRegistry — single emit covering every
        // forward and compensation command type across all sagas in the consumer
        // assembly. Conditional on ZeroAlloc.Serialisation being referenced — the
        // registry uses ISerializer<T> to deserialize outbox payloads, so without
        // the package the emitted code wouldn't compile. Saga consumers that
        // don't use the outbox bridge see no change in generator output.
        var serialisationReferenced = context.CompilationProvider
            .Select(static (compilation, _) =>
                compilation.GetTypeByMetadataName("ZeroAlloc.Serialisation.ZeroAllocSerializableAttribute") is not null)
            .WithTrackingName(TrackingNames.SerialisationReferenced);

        // Per-compilation MediatorSagaCommandDispatcher — single emit covering every
        // [Step] command type across all sagas in the consumer assembly. Lives in the
        // consumer's compilation so it can reference IMediator directly (which is
        // emitted per-assembly by the Mediator source generator).
        context.RegisterSourceOutput(
            allModels,
            static (spc, results) => MediatorSagaCommandDispatcherEmitter.Emit(spc, results));

        // Per-compilation GeneratedSagaCommandSource — the SagaCommandSource every With{Saga}()
        // adds to the builder, so sagas in several assemblies each dispatch their own commands
        // (#176). It overrides DispatchSerializedAsync, forwarding to SagaCommandRegistry, when
        // (and only when) the registry is also being emitted.
        context.RegisterSourceOutput(
            allModels.Combine(serialisationReferenced),
            static (spc, tuple) =>
            {
                var (results, hasSerialisation) = tuple;
                SagaCommandSourceEmitter.Emit(spc, results, registryAlsoEmitted: hasSerialisation);
            });

        context.RegisterSourceOutput(
            allModels.Combine(serialisationReferenced),
            static (spc, tuple) =>
            {
                var (results, hasSerialisation) = tuple;
                if (!hasSerialisation) return;
                SagaCommandRegistryEmitter.Emit(spc, results);
            });

        // The generator does not attach [ZeroAllocSerializable] to step command types: Roslyn
        // runs every source generator against the same input compilation, so
        // ZeroAlloc.Serialisation's generator would never see it (#207). The user applies the
        // attribute or registers an ISerializer<T> for each outbox step command.

        // ZASAGA017 - fired only when ZeroAlloc.Serialisation is referenced, for step command
        // types declared in a referenced assembly. The serializer has to come from that
        // assembly or from the user's own ISerializer<T> registration.
        context.RegisterSourceOutput(
            allModels.Combine(serialisationReferenced),
            static (spc, tuple) =>
            {
                var (results, hasSerialisation) = tuple;
                if (!hasSerialisation) return;

                // De-dupe by command type FQN so a command used by several steps or sagas
                // is reported once.
                var reportedCrossAssembly = new HashSet<string>(System.StringComparer.Ordinal);

                foreach (var result in results)
                {
                    var model = result.Model;
                    if (model is null) continue;
                    foreach (var step in model.Steps)
                    {
                        // The type's declaration is not in this compilation, so the diagnostic
                        // points at the [Step] method's return type, which names it.
                        if (step.CommandTypeIsInOwnAssembly == false
                            && reportedCrossAssembly.Add(step.CommandTypeFqn))
                        {
                            spc.ReportDiagnostic(Diagnostic.Create(
                                SagaDiagnostics.StepCommandTypeCrossAssembly,
                                location: step.CommandTypeLocation?.ToLocation(),
                                step.CommandTypeFqn));
                        }
                    }
                }
            });

        // ZASAGA015: best-effort idempotency hint when a durable backend is wired
        // anywhere in the same compilation. We don't bind the call — we look for
        // any invocation whose name starts with WithEfCoreStore / WithRedisStore,
        // and emit one diagnostic per [Saga] in the compilation, at the saga's class
        // name, so #pragma warning disable ZASAGA015 around a saga suppresses it.
        var hasDurableBackend = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsDurableBackendInvocation(node),
                transform: static (ctx, _) => true)
            .Collect()
            .Select(static (arr, _) => arr.Length > 0)
            .WithTrackingName(TrackingNames.DurableBackendReferenced);

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
                    location: result.Model.ClassNameLocation?.ToLocation(),
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

    private static void ReportCrossSagaDiagnostics(SourceProductionContext spc, EquatableArray<SagaExtractResult> results)
    {
        // For every event-type observed by a saga's correlation methods, gather
        // (saga name, key type, [CorrelationKey] method). If two sagas observe the
        // same event with different key types, report ZASAGA013 once per pair, at
        // the later saga's [CorrelationKey] method, the one that conflicts, with the
        // earlier saga's method as an additional location.
        var byEvent = new Dictionary<string, List<(string Saga, string KeyType, LocationInfo? Loc)>>(System.StringComparer.Ordinal);
        foreach (var result in results)
        {
            var model = result.Model;
            if (model is null) continue;
            foreach (var corr in model.Correlations)
            {
                if (!byEvent.TryGetValue(corr.EventTypeFqn, out var list))
                {
                    list = new List<(string, string, LocationInfo?)>();
                    byEvent[corr.EventTypeFqn] = list;
                }
                list.Add((model.ClassName, model.CorrelationKeyTypeFqn, corr.Location));
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
                        var earlier = entries[i].Loc?.ToLocation();
                        spc.ReportDiagnostic(Diagnostic.Create(
                            SagaDiagnostics.DuplicateSagaCorrelationKeyType,
                            location: entries[j].Loc?.ToLocation(),
                            additionalLocations: earlier is null ? null : new[] { earlier },
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
