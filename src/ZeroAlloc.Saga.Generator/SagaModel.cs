using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ZeroAlloc.Saga.Generator.Diagnostics;

namespace ZeroAlloc.Saga.Generator;

/// <summary>
/// Semantic model the generator extracts from a [Saga] class.
/// </summary>
internal sealed record SagaModel(
    string Namespace,
    string ClassName,
    string Accessibility,
    string CorrelationKeyTypeFqn,
    IReadOnlyList<StepInfo> Steps,
    IReadOnlyList<CorrelationInfo> Correlations,
    IReadOnlyList<string> CompensateOnEventFqns,
    IReadOnlyList<StateFieldInfo> StateFields)
{
    /// <summary>
    /// Extracts the saga model and any authoring-time diagnostics. The model is
    /// returned non-null only when the saga shape is sufficiently valid that emit
    /// can still produce something meaningful; otherwise the diagnostics list
    /// describes why and the model is null. Diagnostics are reported by
    /// <see cref="SagaGenerator"/> via <c>SourceProductionContext.ReportDiagnostic</c>.
    /// </summary>
    public static SagaExtractResult From(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol)
        {
            return new SagaExtractResult(null, diagnostics.ToImmutable());
        }
        ct.ThrowIfCancellationRequested();

        var classDecl = ctx.TargetNode as ClassDeclarationSyntax;
        var classNameLocation = classDecl?.Identifier.GetLocation() ?? classSymbol.Locations.FirstOrDefault();

        // ── ZASAGA001: must be partial ──────────────────────────────────────
        var isPartial = classDecl?.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)) ?? false;
        if (!isPartial)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                SagaDiagnostics.SagaClassMustBePartial,
                classNameLocation,
                classSymbol.Name));
        }

        // ── ZASAGA002: not static / abstract / generic / nested ─────────────
        var disallowed = new List<string>();
        if (classSymbol.IsStatic) disallowed.Add("static");
        if (classSymbol.IsAbstract) disallowed.Add("abstract");
        if (classSymbol.IsGenericType) disallowed.Add("generic");
        if (classSymbol.ContainingType is not null) disallowed.Add("nested");
        if (disallowed.Count > 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                SagaDiagnostics.SagaClassUnsupportedShape,
                classNameLocation,
                classSymbol.Name,
                string.Join("/", disallowed)));
        }

        // ── ZASAGA003: needs accessible parameterless ctor ──────────────────
        // A class with no explicit ctor declaration gets an implicit public one — accept that.
        // Otherwise look for a parameterless one with public/internal accessibility.
        if (!classSymbol.IsStatic && !classSymbol.IsAbstract)
        {
            var explicitCtors = classSymbol.InstanceConstructors
                .Where(c => !c.IsImplicitlyDeclared)
                .ToList();
            if (explicitCtors.Count > 0)
            {
                var hasAccessibleParameterless = explicitCtors.Any(c =>
                    c.Parameters.Length == 0 &&
                    (c.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public ||
                     c.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Internal));
                if (!hasAccessibleParameterless)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.SagaMissingParameterlessCtor,
                        classNameLocation,
                        classSymbol.Name));
                }
            }
        }

        var ns = classSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : classSymbol.ContainingNamespace.ToDisplayString();

        var name = classSymbol.Name;
        var accessibility = classSymbol.DeclaredAccessibility switch
        {
            Microsoft.CodeAnalysis.Accessibility.Public => "public",
            Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
            _ => "internal",
        };

        var steps = new List<StepInfo>();
        var correlations = new List<CorrelationInfo>();
        string? correlationKeyType = null;

        foreach (var member in classSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();

            // [CorrelationKey]
            var corrAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "ZeroAlloc.Saga.CorrelationKeyAttribute");
            if (corrAttr is not null)
            {
                var memberLoc = member.Locations.FirstOrDefault();

                // ZASAGA006: must be 'TKey M(TEvent e)'
                if (member.Parameters.Length != 1 || member.ReturnsVoid)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CorrelationKeyBadSignature,
                        memberLoc,
                        member.Name));
                    continue;
                }

                var keyType = StripGlobalPrefix(member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                if (correlationKeyType is null)
                {
                    correlationKeyType = keyType;
                }
                else if (!string.Equals(correlationKeyType, keyType, System.StringComparison.Ordinal))
                {
                    // ZASAGA005
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.InconsistentCorrelationKeyTypes,
                        memberLoc,
                        classSymbol.Name,
                        correlationKeyType,
                        keyType));
                    continue;
                }

                var eventTypeFqn = StripGlobalPrefix(member.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                correlations.Add(new CorrelationInfo(member.Name, eventTypeFqn));

                // ZASAGA011: heuristic — body looks like it mutates state.
                if (CorrelationKeyAppearsToMutate(member, ct))
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CorrelationKeyMutatesState,
                        memberLoc,
                        member.Name));
                }
                continue;
            }

            // [Step]
            var stepAttr = member.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == "ZeroAlloc.Saga.StepAttribute");
            if (stepAttr is not null)
            {
                var memberLoc = member.Locations.FirstOrDefault();

                // ZASAGA008: shape must be 'TCommand M(TEvent e)'.
                if (member.Parameters.Length != 1 || member.ReturnsVoid)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.StepBadSignature,
                        memberLoc,
                        member.Name));
                    continue;
                }

                int order = 0;
                string? compensateName = null;
                string? compensateOnFqn = null;
                foreach (var arg in stepAttr.NamedArguments)
                {
                    if (string.Equals(arg.Key, "Order", System.StringComparison.Ordinal) && arg.Value.Value is int o)
                        order = o;
                    else if (string.Equals(arg.Key, "Compensate", System.StringComparison.Ordinal) && arg.Value.Value is string s)
                        compensateName = s;
                    else if (string.Equals(arg.Key, "CompensateOn", System.StringComparison.Ordinal) && arg.Value.Value is INamedTypeSymbol t)
                        compensateOnFqn = StripGlobalPrefix(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }

                var eventTypeFqn = StripGlobalPrefix(member.Parameters[0].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                var commandTypeFqn = StripGlobalPrefix(member.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                steps.Add(new StepInfo(order, member.Name, eventTypeFqn, commandTypeFqn, compensateName, compensateOnFqn, memberLoc));
            }
        }

        // ── ZASAGA004: each step's input event must have a [CorrelationKey] ─
        var correlatedEvents = new HashSet<string>(correlations.Select(c => c.EventTypeFqn), System.StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!correlatedEvents.Contains(step.EventTypeFqn))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    SagaDiagnostics.StepEventLacksCorrelationKey,
                    step.Location,
                    step.MethodName,
                    step.EventTypeFqn));
            }
        }

        // ── ZASAGA007: step Order values must be 1..N contiguous ────────────
        if (steps.Count > 0)
        {
            var orders = steps.Select(s => s.Order).OrderBy(x => x).ToList();
            var expected = Enumerable.Range(1, steps.Count).ToList();
            if (!orders.SequenceEqual(expected))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    SagaDiagnostics.StepOrderGapsOrDuplicates,
                    classNameLocation,
                    classSymbol.Name,
                    string.Join(", ", orders)));
            }
        }

        // ── ZASAGA009 / ZASAGA010 / ZASAGA012 ───────────────────────────────
        var allMembers = classSymbol.GetMembers().OfType<IMethodSymbol>().ToList();
        foreach (var step in steps)
        {
            // ZASAGA009: Compensate target must exist and be parameterless, non-void.
            if (step.CompensateMethodName is not null)
            {
                var target = allMembers.FirstOrDefault(m => string.Equals(m.Name, step.CompensateMethodName, System.StringComparison.Ordinal));
                var ok = target is not null
                    && target.Parameters.Length == 0
                    && !target.ReturnsVoid;
                if (!ok)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CompensateMethodMissingOrBadShape,
                        step.Location,
                        step.MethodName,
                        step.CompensateMethodName));
                }
            }

            // ZASAGA010: CompensateOn event needs a CorrelationKey method.
            if (step.CompensateOnEventTypeFqn is not null
                && !correlatedEvents.Contains(step.CompensateOnEventTypeFqn))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    SagaDiagnostics.CompensateOnEventLacksCorrelationKey,
                    step.Location,
                    step.MethodName,
                    step.CompensateOnEventTypeFqn));
            }

            // ZASAGA012: Compensate without CompensateOn = dead code, *unless* a
            // later step in the saga has its own CompensateOn — the reverse cascade
            // will still drive this method as part of compensating prior steps.
            if (step.CompensateMethodName is not null && step.CompensateOnEventTypeFqn is null)
            {
                var hasLaterCompensateOn = steps.Any(s => s.Order > step.Order && s.CompensateOnEventTypeFqn is not null);
                if (!hasLaterCompensateOn)
                {
                    diagnostics.Add(DiagnosticInfo.Create(
                        SagaDiagnostics.CompensateWithoutCompensateOn,
                        step.Location,
                        step.MethodName,
                        step.CompensateMethodName));
                }
            }
        }

        // If we cannot determine a correlation key type or have no steps, no model.
        if (correlationKeyType is null || steps.Count == 0)
        {
            return new SagaExtractResult(null, diagnostics.ToImmutable());
        }

        // Sort steps by Order to get a stable forward sequence.
        steps.Sort((a, b) => a.Order.CompareTo(b.Order));

        var compensateOn = steps
            .Where(s => s.CompensateOnEventTypeFqn is not null)
            .Select(s => s.CompensateOnEventTypeFqn!)
            .Distinct(System.StringComparer.Ordinal)
            .ToList();

        // Extract state fields/properties for Snapshot/Restore emission.
        // Reports ZASAGA014 for unsupported field types.
        var stateFields = ExtractStateFields(classSymbol, classNameLocation, diagnostics, ct);

        var model = new SagaModel(ns, name, accessibility, correlationKeyType, steps, correlations, compensateOn, stateFields);
        return new SagaExtractResult(model, diagnostics.ToImmutable());
    }

    /// <summary>
    /// Walks the saga class's instance fields/properties and produces a stable,
    /// declaration-order list of state members for Snapshot/Restore emission.
    /// Reports ZASAGA014 for any member whose type is not supported by the
    /// v1.1 byte serializer.
    /// </summary>
    private static IReadOnlyList<StateFieldInfo> ExtractStateFields(
        INamedTypeSymbol classSymbol,
        Location? classNameLocation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        CancellationToken ct)
    {
        var result = new List<StateFieldInfo>();

        // Walk members in declaration order to produce a stable wire layout.
        // Skip auto-generated members, the Fsm property, statics, and members
        // marked [NotSagaState].
        foreach (var member in classSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (member.IsStatic) continue;
            if (member.IsImplicitlyDeclared) continue;
            if (string.Equals(member.Name, "Fsm", System.StringComparison.Ordinal)) continue;
            if (HasNotSagaStateAttribute(member)) continue;

            switch (member)
            {
                case IPropertySymbol prop:
                {
                    if (prop.SetMethod is null) continue;
                    if (prop.IsIndexer) continue;
                    var loc = prop.Locations.FirstOrDefault() ?? classNameLocation;
                    var info = ClassifyType(prop.Type, prop.Name, classSymbol.Name, loc, diagnostics);
                    if (info is not null) result.Add(info);
                    break;
                }
                case IFieldSymbol field:
                {
                    // Skip backing fields for auto-properties (they appear as
                    // implicitly declared above) and readonly/const members.
                    if (field.IsReadOnly || field.IsConst) continue;
                    if (field.AssociatedSymbol is IPropertySymbol) continue;
                    if (field.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public &&
                        field.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Internal) continue;
                    var loc = field.Locations.FirstOrDefault() ?? classNameLocation;
                    var info = ClassifyType(field.Type, field.Name, classSymbol.Name, loc, diagnostics);
                    if (info is not null) result.Add(info);
                    break;
                }
            }
        }

        return result;
    }

    private static bool HasNotSagaStateAttribute(ISymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            var name = attr.AttributeClass?.ToDisplayString();
            if (string.Equals(name, "ZeroAlloc.Saga.NotSagaStateAttribute", System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static StateFieldInfo? ClassifyType(
        ITypeSymbol type,
        string memberName,
        string className,
        Location? location,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var typeFqn = StripGlobalPrefix(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        // Handle Nullable<T> → recurse into T.
        if (type is INamedTypeSymbol named && named.IsGenericType
            && named.ConstructedFrom?.SpecialType == SpecialType.System_Nullable_T)
        {
            var inner = named.TypeArguments[0];
            var innerInfo = ClassifyType(inner, memberName, className, location, diagnostics);
            if (innerInfo is null) return null; // diagnostic already reported
            if (innerInfo.Kind == StateFieldKind.Unsupported) return innerInfo;
            // Compose the nullable wrapper.
            return new StateFieldInfo(
                memberName,
                typeFqn,
                StateFieldKind.Nullable,
                EnumUnderlyingKind: innerInfo.EnumUnderlyingKind,
                IsTypedId: innerInfo.IsTypedId,
                InnerKind: innerInfo.Kind,
                TypedIdValuePrimitiveKind: innerInfo.TypedIdValuePrimitiveKind,
                Location: location);
        }

        // Enums → store the underlying primitive's kind.
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumNamed)
        {
            var underlying = enumNamed.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32;
            var underlyingKind = MapPrimitiveSpecial(underlying);
            if (underlyingKind is null)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    Diagnostics.SagaDiagnostics.UnsupportedFieldType,
                    location, memberName, className, typeFqn));
                return new StateFieldInfo(memberName, typeFqn, StateFieldKind.Unsupported, null, false, null, null, location);
            }
            return new StateFieldInfo(memberName, typeFqn, StateFieldKind.Enum, underlyingKind.Value, false, null, null, location);
        }

        // Primitives & well-known types via SpecialType + name match.
        var primitiveKind = MapPrimitiveSpecial(type.SpecialType);
        if (primitiveKind is not null)
        {
            return new StateFieldInfo(memberName, typeFqn, primitiveKind.Value, null, false, null, null, location);
        }

        // System.* well-known types not represented by SpecialType.
        switch (typeFqn)
        {
            case "System.Decimal":
                return new StateFieldInfo(memberName, typeFqn, StateFieldKind.Decimal, null, false, null, null, location);
            case "System.DateTime":
                return new StateFieldInfo(memberName, typeFqn, StateFieldKind.DateTime, null, false, null, null, location);
            case "System.DateTimeOffset":
                return new StateFieldInfo(memberName, typeFqn, StateFieldKind.DateTimeOffset, null, false, null, null, location);
            case "System.TimeSpan":
                return new StateFieldInfo(memberName, typeFqn, StateFieldKind.TimeSpan, null, false, null, null, location);
            case "System.Guid":
                return new StateFieldInfo(memberName, typeFqn, StateFieldKind.Guid, null, false, null, null, location);
        }

        // byte[] — supported as length-prefixed bytes. NRT-annotated `byte[]?` uses
        // the dedicated nullable kind so the emitter routes to the WriteBytes(byte[]?)
        // overload and ReadBytesNullable(), preserving null-vs-empty round-trip.
        if (type is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
        {
            var kind = type.NullableAnnotation == NullableAnnotation.Annotated
                ? StateFieldKind.ByteArrayNullable
                : StateFieldKind.ByteArray;
            return new StateFieldInfo(memberName, typeFqn, kind, null, false, null, null, location);
        }

        // [TypedId] structs — read .Value primitive.
        if (type.IsValueType && IsTypedIdAttributedType(type, out var valuePrimitiveKind))
        {
            return new StateFieldInfo(memberName, typeFqn, StateFieldKind.TypedId, null, true, null, valuePrimitiveKind, location);
        }

        // Common pattern: a positional `record struct Foo(TPrim Value)` (or named other than
        // Value) used as a typed wrapper around a primitive. Treat the same as [TypedId] —
        // the wire encoding is the underlying primitive, and rehydrate with the
        // single-arg primary ctor.
        if (type.IsValueType && type is INamedTypeSymbol nts
            && nts.IsRecord
            && TryFindRecordPositionalPrimitive(nts, out var ctorParamName, out var ctorPrimKind))
        {
            return new StateFieldInfo(
                memberName, typeFqn, StateFieldKind.TypedId, null,
                IsTypedId: true,
                InnerKind: null,
                TypedIdValuePrimitiveKind: ctorPrimKind,
                Location: location)
            { RecordPositionalParamName = ctorParamName };
        }

        // Anything else: ZASAGA014.
        diagnostics.Add(DiagnosticInfo.Create(
            Diagnostics.SagaDiagnostics.UnsupportedFieldType,
            location, memberName, className, typeFqn));
        return new StateFieldInfo(memberName, typeFqn, StateFieldKind.Unsupported, null, false, null, null, location);
    }

    /// <summary>
    /// Detects the <c>record struct Foo(TPrim Bar)</c> shape — a struct with a
    /// primary constructor of one parameter whose type is a supported primitive.
    /// Captures the parameter name so the emitter can read it back via the
    /// generated property.
    /// </summary>
    private static bool TryFindRecordPositionalPrimitive(
        INamedTypeSymbol nts,
        out string? paramName,
        out StateFieldKind? primKind)
    {
        paramName = null;
        primKind = null;
        // Look for the primary constructor — Roslyn synthesises one for record
        // structs with positional parameters; we approximate by finding a public
        // single-param ctor.
        foreach (var ctor in nts.InstanceConstructors)
        {
            if (ctor.IsImplicitlyDeclared && nts.InstanceConstructors.Length > 1) continue;
            if (ctor.Parameters.Length != 1) continue;
            var p = ctor.Parameters[0];
            var k = MapPrimitiveSpecial(p.Type.SpecialType);
            if (k is null && string.Equals(StripGlobalPrefix(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), "System.Guid", System.StringComparison.Ordinal))
                k = StateFieldKind.Guid;
            if (k is null) continue;
            paramName = p.Name;
            primKind = k;
            return true;
        }
        return false;
    }

    private static StateFieldKind? MapPrimitiveSpecial(SpecialType st) => st switch
    {
        SpecialType.System_Byte => StateFieldKind.Byte,
        SpecialType.System_SByte => StateFieldKind.SByte,
        SpecialType.System_Int16 => StateFieldKind.Int16,
        SpecialType.System_UInt16 => StateFieldKind.UInt16,
        SpecialType.System_Int32 => StateFieldKind.Int32,
        SpecialType.System_UInt32 => StateFieldKind.UInt32,
        SpecialType.System_Int64 => StateFieldKind.Int64,
        SpecialType.System_UInt64 => StateFieldKind.UInt64,
        SpecialType.System_Single => StateFieldKind.Single,
        SpecialType.System_Double => StateFieldKind.Double,
        SpecialType.System_Decimal => StateFieldKind.Decimal,
        SpecialType.System_Boolean => StateFieldKind.Boolean,
        SpecialType.System_String => StateFieldKind.String,
        SpecialType.System_DateTime => StateFieldKind.DateTime,
        _ => null,
    };

    private static bool IsTypedIdAttributedType(ITypeSymbol type, out StateFieldKind? valueKind)
    {
        valueKind = null;
        foreach (var attr in type.GetAttributes())
        {
            var name = attr.AttributeClass?.ToDisplayString();
            if (string.Equals(name, "ZeroAlloc.ValueObjects.TypedIdAttribute", System.StringComparison.Ordinal))
            {
                // Look for a public Value property to determine underlying primitive.
                foreach (var m in type.GetMembers("Value"))
                {
                    if (m is IPropertySymbol p && p.GetMethod is not null)
                    {
                        var k = MapPrimitiveSpecial(p.Type.SpecialType);
                        if (k is null && string.Equals(StripGlobalPrefix(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), "System.Guid", System.StringComparison.Ordinal))
                            k = StateFieldKind.Guid;
                        valueKind = k;
                        return true;
                    }
                }
                // [TypedId] without a Value property — still mark as TypedId; emitter will fall back to Guid.
                valueKind = StateFieldKind.Guid;
                return true;
            }
        }
        return false;
    }

    private static bool CorrelationKeyAppearsToMutate(IMethodSymbol method, CancellationToken ct)
    {
        // Heuristic syntactic check: scan the syntax tree of any declaration for
        // assignment expressions or compound-assignment operators inside the body.
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            var node = declRef.GetSyntax(ct);
            if (node is null) continue;
            foreach (var d in node.DescendantNodes())
            {
                if (d is AssignmentExpressionSyntax) return true;
                if (d is PostfixUnaryExpressionSyntax pu &&
                    (pu.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PlusPlusToken) ||
                     pu.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MinusMinusToken)))
                    return true;
                if (d is PrefixUnaryExpressionSyntax pre &&
                    (pre.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PlusPlusToken) ||
                     pre.OperatorToken.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MinusMinusToken)))
                    return true;
            }
        }
        return false;
    }

    private static string StripGlobalPrefix(string s)
        => s.StartsWith("global::", System.StringComparison.Ordinal) ? s.Substring("global::".Length) : s;
}

internal sealed record StepInfo(
    int Order,
    string MethodName,
    string EventTypeFqn,
    string CommandTypeFqn,
    string? CompensateMethodName,
    string? CompensateOnEventTypeFqn,
    Location? Location = null);

/// <summary>
/// Information about a single saga state member (field or property) the
/// generator will emit Snapshot/Restore calls for.
/// </summary>
internal sealed record StateFieldInfo(
    string MemberName,
    string TypeFqn,
    StateFieldKind Kind,
    /// <summary>Underlying primitive kind for enums (e.g. Int32). Null otherwise.</summary>
    StateFieldKind? EnumUnderlyingKind,
    bool IsTypedId,
    /// <summary>For Nullable&lt;T&gt;: the wrapped Kind. Null otherwise.</summary>
    StateFieldKind? InnerKind,
    /// <summary>For TypedId: the kind of the .Value primitive.</summary>
    StateFieldKind? TypedIdValuePrimitiveKind,
    Location? Location)
{
    /// <summary>
    /// For TypedId-shaped value types reachable via a single positional record
    /// ctor (e.g. <c>record struct OrderId(int Value)</c>), the name of the
    /// underlying property — used by the emitter to read back via
    /// <c>field.&lt;Param&gt;</c> in <c>Snapshot()</c>. Null for [TypedId]-attributed
    /// types where <c>.Value</c> is the canonical accessor.
    /// </summary>
    public string? RecordPositionalParamName { get; init; }
}

internal enum StateFieldKind
{
    Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64,
    Single, Double, Decimal, Boolean,
    String,
    DateTime, DateTimeOffset, TimeSpan, Guid,
    Enum,
    Nullable,
    TypedId,
    ByteArray,
    /// <summary>
    /// <c>byte[]?</c> with NRT-annotated nullable. Distinct from
    /// <see cref="ByteArray"/> so the generator can emit the nullable
    /// writer/reader overloads that round-trip null vs empty.
    /// </summary>
    ByteArrayNullable,
    Unsupported,
}

internal sealed record CorrelationInfo(
    string MethodName,
    string EventTypeFqn);

/// <summary>
/// Carries the optional model plus any diagnostics surfaced during extraction.
/// </summary>
internal sealed record SagaExtractResult(
    SagaModel? Model,
    ImmutableArray<DiagnosticInfo> Diagnostics);
