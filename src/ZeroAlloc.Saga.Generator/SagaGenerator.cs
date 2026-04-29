using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Source generator for [Saga] partial classes. For each saga, emits:
///   1. {SagaName}Fsm.g.cs            — inline FSM partial (state, triggers, TryFire)
///   2. {SagaName}.g.cs               — partial-class completion attaching the Fsm property
///   3. {SagaName}_{Event}_Handler.g.cs — one INotificationHandler per event
///   4. {SagaName}CorrelationDispatch.g.cs — typed event-to-key dispatch
///   5. {SagaName}BuilderExtensions.g.cs — AOT-safe DI registrations + compensation dispatcher
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SagaGenerator : IIncrementalGenerator
{
    private const string SagaAttributeFqn = "ZeroAlloc.Saga.SagaAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var sagas = context.SyntaxProvider.ForAttributeWithMetadataName(
            SagaAttributeFqn,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) => SagaModel.From(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(sagas, static (spc, model) =>
        {
            FsmEmitter.Emit(spc, model);
            PartialCompletionEmitter.Emit(spc, model);
            HandlerEmitter.Emit(spc, model);
            CorrelationDispatchEmitter.Emit(spc, model);
            BuilderExtensionsEmitter.Emit(spc, model);
        });
    }
}
