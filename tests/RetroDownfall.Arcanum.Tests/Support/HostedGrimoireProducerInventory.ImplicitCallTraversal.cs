using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using System.Collections.Immutable;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

// The traversal half of the Implicit Call Census: where the compiler calls a member without a written call, the
// census item is resolved to the exact members it runs and each one is traversed at the item, under the Work and
// effect frontiers live where the compiler runs it, or the item is refused.
internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // A census depends only on the syntax and the semantic model that binds it, never on a traversal state.
        private readonly Dictionary<SemanticModel, Dictionary<SyntaxNode, Dictionary<SyntaxNode, List<ImplicitCall>>>> implicitCallCensus = new(ReferenceEqualityComparer.Instance);

        // The syntax walk of a member body visits every node except the inside of a lambda or a local function, so
        // an anchor the walk would not visit is moved to the region itself, which it always visits.
        private static bool IsVisitedByTraversal(SyntaxNode region, SyntaxNode anchor)
        {
            if (anchor == region)
            {
                return true;
            }

            if (!region.Span.Contains(anchor.Span) || anchor.SyntaxTree != region.SyntaxTree)
            {
                return false;
            }

            for (SyntaxNode? ancestor = anchor.Parent; ancestor is not null && ancestor != region; ancestor = ancestor.Parent)
            {
                if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                {
                    return false;
                }
            }

            return true;
        }

        private IReadOnlyList<ImplicitCall> ImplicitCallsAt(AuthoredMember member, SyntaxNode region, SyntaxNode node)
        {
            if (!implicitCallCensus.TryGetValue(member.Model, out Dictionary<SyntaxNode, Dictionary<SyntaxNode, List<ImplicitCall>>>? regions))
            {
                implicitCallCensus.Add(member.Model, regions = new(ReferenceEqualityComparer.Instance));
            }

            if (!regions.TryGetValue(region, out Dictionary<SyntaxNode, List<ImplicitCall>>? byAnchor))
            {
                byAnchor = new(ReferenceEqualityComparer.Instance);

                foreach (ImplicitCall census in CollectImplicitCalls(member.Model, region))
                {
                    ImplicitCall call = IsVisitedByTraversal(region, census.Anchor)
                        ? census
                        : new(ImplicitCallKind.Unsupported, census.Source, region, [], "HOSTED_SITE_UNCLASSIFIED", "An implicit call at " + Location(census.Anchor) + " lies outside the syntax the traversal visits.");

                    if (!byAnchor.TryGetValue(call.Anchor, out List<ImplicitCall>? list))
                    {
                        byAnchor.Add(call.Anchor, list = []);
                    }

                    list.Add(call);
                }

                regions.Add(region, byAnchor);
            }

            return byAnchor.TryGetValue(node, out List<ImplicitCall>? calls) ? calls : [];
        }

        private sealed record ImplicitTargets(
            IReadOnlyList<AuthoredMember> Members,
            IReadOnlyList<IMethodSymbol> Framework,
            IReadOnlyList<string> Unresolved);

        private static bool IsSealedOrValue(ITypeSymbol? type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Struct } or INamedTypeSymbol { TypeKind: TypeKind.Class, IsSealed: true };

        // Accumulates the members one implicit call runs: authored bodies to traverse, framework members to classify,
        // and members nothing here can follow.
        private sealed class ImplicitTargetCollector(ProducerGraph graph, AuthoredMember member)
        {
            private readonly HashSet<ISymbol> visited = new(SymbolEqualityComparer.Default);

            internal List<AuthoredMember> Members { get; } = [];

            internal List<IMethodSymbol> Framework { get; } = [];

            internal List<string> Unresolved { get; } = [];

            internal ImplicitTargets Result => new(Members, Framework, Unresolved);

            // The implementation the compiler runs for a call to the slot, which is the implementation itself when the
            // caller has already resolved the slot against the runtime type.
            internal void Add(ISymbol? implementation, IMethodSymbol slot)
            {
                if (implementation is not IMethodSymbol method)
                {
                    Unresolved.Add(Normalize(slot));

                    return;
                }

                if (!visited.Add(method.OriginalDefinition))
                {
                    return;
                }

                if (graph.EnumerationAuthoredMember(method, member.Model.Compilation) is { } authored)
                {
                    if (authored.Syntax is AccessorDeclarationSyntax { Body: null, ExpressionBody: null })
                    {
                        // An auto-property accessor reads or writes its backing field and runs no code.
                        return;
                    }

                    if (HasEnumerationBody(authored))
                    {
                        Members.Add(authored);
                    }
                    else
                    {
                        Unresolved.Add(Normalize(method));
                    }

                    return;
                }

                if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet && method.IsImplicitlyDeclared && method.AssociatedSymbol is IPropertySymbol { IsImplicitlyDeclared: true } or IPropertySymbol { DeclaringSyntaxReferences.Length: > 0 })
                {
                    // A compiler-generated accessor of an auto-property, such as a positional record's, runs no code.
                    return;
                }

                if (method.MethodKind == MethodKind.Constructor && !method.IsStatic && method.ContainingType.Locations.Any(static location => location.IsInSource))
                {
                    graph.AddConstructionEntry(member, method, this);

                    return;
                }

                if (method.IsImplicitlyDeclared && method.ContainingType is { } owner && (owner.IsRecord || owner.IsAnonymousType) && owner.Locations.Any(static location => location.IsInSource))
                {
                    graph.AddSynthesizedMember(member, method, this);

                    return;
                }

                // A member with source but no authored body is abstract, extern or compiler-generated, and nothing
                // here follows what it runs.
                if (method.Locations.Any(static location => location.IsInSource))
                {
                    Unresolved.Add(Normalize(method));

                    return;
                }

                Framework.Add(method);
            }
        }

        // The members the compiler's call to the step's slot runs. A static member, a constructor and a member that
        // cannot be overridden are their own implementation; any other slot is resolved on the exact runtime types of
        // the value it is called on, which are the static type when that type is a struct or sealed, the types the value
        // is proved to have otherwise, and unresolved when nothing proves them.
        private ImplicitTargets ResolveImplicitStep(AuthoredMember member, ImplicitCallStep step)
        {
            ImplicitTargetCollector targets = new(this, member);

            IMethodSymbol slot = step.Method;

            bool fixedImplementation = slot.IsStatic
                || slot.MethodKind == MethodKind.Constructor
                || !slot.IsVirtual && !slot.IsAbstract && !slot.IsOverride && slot.ContainingType.TypeKind != TypeKind.Interface;

            if (fixedImplementation)
            {
                targets.Add(slot, slot);

                return targets.Result;
            }

            ITypeSymbol? receiverType = step.ReceiverType;

            List<INamedTypeSymbol> candidates = [];

            if (IsSealedOrValue(receiverType))
            {
                candidates.Add((INamedTypeSymbol)receiverType!);
            }
            else if (step.Receiver is { } receiver
                && receiver.SyntaxTree == member.Model.SyntaxTree
                && receiverType is not null)
            {
                EnumerationProof proof = new();

                if (ProveEnumerationLeaf(receiver, member.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member, proof))
                {
                    candidates.AddRange(proof.Candidates.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>());

                    if (candidates.Count == 0 && IsClosedFrameworkEnumerationType(receiverType))
                    {
                        targets.Add(slot, slot);

                        return targets.Result;
                    }
                }
            }

            if (candidates.Count == 0)
            {
                targets.Unresolved.Add(Normalize(slot));

                return targets.Result;
            }

            foreach (INamedTypeSymbol candidate in candidates)
            {
                targets.Add(EnumerationImplementation(candidate, slot), slot);
            }

            return targets.Result;
        }

        // The construction a constructor without a declaration of its own runs: the instance initializers of its
        // type, then its base constructor. A constructor declared with a body gets the same preamble when it is traversed.
        private void AddConstructionEntry(AuthoredMember member, IMethodSymbol constructor, ImplicitTargetCollector targets)
        {
            INamedTypeSymbol type = constructor.ContainingType;

            if (type.TypeKind != TypeKind.Class && !type.IsRecord)
            {
                return;
            }

            targets.Members.AddRange(InitializerMembers(type, constructor));

            if (constructor.Parameters is [{ } copied] && SymbolEqualityComparer.Default.Equals(copied.Type, type) && type.IsRecord)
            {
                // A record's copy constructor copies state and calls the copy constructor of its base record.
                if (type.BaseType is { IsRecord: true } baseRecord && CopyConstructor(baseRecord) is { } baseCopy)
                {
                    targets.Add(baseCopy, baseCopy);
                }

                return;
            }

            if (PrimaryConstructorBase(constructor) is { } primaryBase)
            {
                targets.Add(primaryBase, primaryBase);
            }
            else if (ImplicitBaseConstructor(type) is { } implicitBase)
            {
                targets.Add(implicitBase, implicitBase);
            }
        }

        private static IMethodSymbol? CopyConstructor(INamedTypeSymbol record) =>
            record.InstanceConstructors.FirstOrDefault(constructor => constructor.Parameters is [{ } parameter] && SymbolEqualityComparer.Default.Equals(parameter.Type, record));

        private static IMethodSymbol? ImplicitBaseConstructor(INamedTypeSymbol type)
        {
            if (type.BaseType is not { SpecialType: not SpecialType.System_Object } baseType)
            {
                return null;
            }

            return baseType.InstanceConstructors
                .Where(static constructor => constructor.Parameters.All(static parameter => parameter.IsOptional || parameter.IsParams))
                .OrderBy(static constructor => constructor.Parameters.Length)
                .FirstOrDefault();
        }

        // The base constructor a primary constructor's base type list selects: `class D(int x) : B(x)`.
        private IMethodSymbol? PrimaryConstructorBase(IMethodSymbol constructor)
        {
            foreach (SyntaxReference reference in constructor.ContainingType.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is TypeDeclarationSyntax { BaseList.Types: { } types } declaration
                    && types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault() is { } baseType
                    && semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? model)
                    && model.GetSymbolInfo(baseType).Symbol is IMethodSymbol selected)
                {
                    return selected;
                }
            }

            return null;
        }

        // The instance field and property initializers of a type, as members whose body is the initializer expression.
        // An initializer that cannot reach a producer site adds nothing, so it is not traversed.
        private IReadOnlyList<AuthoredMember> InitializerMembers(INamedTypeSymbol type, IMethodSymbol constructor)
        {
            List<AuthoredMember> initializers = [];

            foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is not TypeDeclarationSyntax declaration || !semanticModels.TryGetValue(declaration.SyntaxTree, out SemanticModel? model))
                {
                    continue;
                }

                foreach (MemberDeclarationSyntax declared in declaration.Members)
                {
                    IEnumerable<ExpressionSyntax> values = declared switch
                    {
                        FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword) =>
                            field.Declaration.Variables.Select(static variable => variable.Initializer?.Value).OfType<ExpressionSyntax>(),
                        PropertyDeclarationSyntax { Initializer.Value: { } value } property when !property.Modifiers.Any(SyntaxKind.StaticKeyword) => [value],
                        EventFieldDeclarationSyntax @event when !@event.Modifiers.Any(SyntaxKind.StaticKeyword) =>
                            @event.Declaration.Variables.Select(static variable => variable.Initializer?.Value).OfType<ExpressionSyntax>(),
                        _ => [],
                    };

                    foreach (ExpressionSyntax value in values)
                    {
                        AuthoredMember initializer = new(constructor, value, model);

                        if (ContainsPotentialProducerSite(initializer))
                        {
                            initializers.Add(initializer);
                        }
                    }
                }
            }

            return initializers;
        }

        // The members a compiler-generated record or anonymous type member runs. Generated members run only the
        // accessors the record declares itself: positional properties and printed members are auto-properties unless
        // the record declares them, and a declared accessor is authored code.
        private void AddSynthesizedMember(AuthoredMember member, IMethodSymbol synthesized, ImplicitTargetCollector targets)
        {
            INamedTypeSymbol record = synthesized.ContainingType;

            void Getters(IEnumerable<ISymbol> candidates)
            {
                foreach (ISymbol candidate in candidates)
                {
                    if (candidate is IPropertySymbol { GetMethod: { } getter, IsStatic: false })
                    {
                        targets.Add(getter, getter);
                    }
                }
            }

            switch (synthesized.Name)
            {
                case "Deconstruct":
                    Getters(PositionalProperties(record));

                    break;

                case "<Clone>$":
                    if (CopyConstructor(record) is { } copy)
                    {
                        targets.Add(copy, copy);
                    }
                    else
                    {
                        targets.Unresolved.Add(Normalize(synthesized));
                    }

                    break;

                case "ToString" or "PrintMembers":
                    for (INamedTypeSymbol? current = record; current is not null && (current.IsRecord || current.IsAnonymousType); current = current.BaseType)
                    {
                        foreach (ISymbol printed in current.GetMembers().Where(static candidate => candidate is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } or IFieldSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, IsImplicitlyDeclared: false }))
                        {
                            ITypeSymbol? printedType = printed switch
                            {
                                IPropertySymbol property => property.Type,
                                IFieldSymbol field => field.Type,
                                _ => null,
                            };

                            if (printed is IPropertySymbol { GetMethod: { } getter })
                            {
                                targets.Add(getter, getter);
                            }

                            if (printedType is not null)
                            {
                                AddFormatting(member, printedType, targets);
                            }
                        }
                    }

                    break;

                default:
                    targets.Unresolved.Add(Normalize(synthesized));

                    break;
            }
        }

        private static IEnumerable<ISymbol> PositionalProperties(INamedTypeSymbol record)
        {
            if (record.InstanceConstructors.FirstOrDefault(static constructor => constructor.Parameters.Length != 0 && constructor.DeclaringSyntaxReferences.All(static reference => reference.GetSyntax() is TypeDeclarationSyntax)) is not { } primary)
            {
                return [];
            }

            return primary.Parameters.SelectMany(parameter => record.GetMembers(parameter.Name).OfType<IPropertySymbol>());
        }

        // The members ToString of a value of the type runs when a record prints it: the framework value types that
        // print nothing authored add nothing, an exact authored type adds its ToString and formatting members, and a
        // type nothing proves is unresolved.
        private void AddFormatting(AuthoredMember member, ITypeSymbol type, ImplicitTargetCollector targets)
        {
            if (IsIntrinsicFormatType(type))
            {
                return;
            }

            if (!IsSealedOrValue(type))
            {
                targets.Unresolved.Add(TypeKey(type) + ".ToString");

                return;
            }

            IMethodSymbol? toString = member.Model.Compilation.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(static method => method.Parameters.Length == 0);

            if (toString is not null)
            {
                targets.Add(EnumerationImplementation(type, toString), toString);
            }
        }

        private bool IsReviewedImplicitFrameworkMember(AuthoredMember member, IMethodSymbol method, SyntaxNode anchor) =>
            IsReviewedFrameworkAwaiterMember(method)
            || IsReviewedFrameworkRangeMember(method)
            || IsReviewedFrameworkInterpolationHandlerMember(method)
            || method.Name == "ToString" && method.ContainingType is { } type && TypeKey(type) is "System.ValueType" && IsExactFrameworkType(type, "System.ValueType", typeof(ValueType).Assembly.GetName())
            || !RequiresExternalBoundaryClassification(method, member.Model.Compilation, anchor, member.Model, member);

        private static bool IsReviewedFrameworkAwaiterMember(IMethodSymbol method)
        {
            if (method.ContainingType is not { } type || !FrameworkAwaiterTypes.Contains(TypeKey(type)))
            {
                return false;
            }

            return method.Name is "GetAwaiter" or "get_IsCompleted" or "GetResult" or "OnCompleted" or "UnsafeOnCompleted"
                && FrameworkAssemblyIdentityMatches(type.ContainingAssembly.Identity, typeof(System.Threading.Tasks.Task).Assembly.GetName())
                && !type.Locations.Any(static location => location.IsInSource);
        }

        private static readonly IReadOnlySet<string> FrameworkAwaiterTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Threading.Tasks.Task",
            "System.Threading.Tasks.Task`1",
            "System.Threading.Tasks.ValueTask",
            "System.Threading.Tasks.ValueTask`1",
            "System.Runtime.CompilerServices.TaskAwaiter",
            "System.Runtime.CompilerServices.TaskAwaiter`1",
            "System.Runtime.CompilerServices.ValueTaskAwaiter",
            "System.Runtime.CompilerServices.ValueTaskAwaiter`1",
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable",
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1",
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter",
            "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1.ConfiguredTaskAwaiter",
            "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable",
            "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1",
            "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter",
            "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1.ConfiguredValueTaskAwaiter",
            "System.Runtime.CompilerServices.YieldAwaitable",
            "System.Runtime.CompilerServices.YieldAwaitable.YieldAwaiter",
        };

        private static bool IsReviewedFrameworkRangeMember(IMethodSymbol method) =>
            method.ContainingType is { } type
            && (IsExactFrameworkType(type, "System.Range", typeof(Range).Assembly.GetName())
                || IsExactFrameworkType(type, "System.Index", typeof(Index).Assembly.GetName()));

        // The framework's own interpolated string handlers format and append in memory.
        private static bool IsReviewedFrameworkInterpolationHandlerMember(IMethodSymbol method) =>
            method.ContainingType is { } type
            && (IsExactFrameworkType(type, "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler", typeof(System.Runtime.CompilerServices.DefaultInterpolatedStringHandler).Assembly.GetName())
                || IsExactFrameworkType(type, "System.Text.StringBuilder.AppendInterpolatedStringHandler", typeof(System.Text.StringBuilder).Assembly.GetName()));

        private TraversalEvidence TraverseImplicitCalls(
            AuthoredMember member,
            IReadOnlyList<ImplicitCall> calls,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            GrimoireWorkKind? workKind,
            string? inheritedWork,
            string? inheritedEffect,
            string? recoveryEffect)
        {
            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            foreach (ImplicitCall call in calls)
            {
                if (call.RefusalCode is { } code)
                {
                    RefuseTraversalCompletion();

                    diagnostics.Add(new(code, Location(call.Anchor), call.RefusalDetail ?? "An implicit call has no exact target."));

                    continue;
                }

                if (call.Kind == ImplicitCallKind.QueryClause)
                {
                    evidence = MergeEvidence(evidence, TraverseQueryExpression(member, call, operationId));

                    continue;
                }

                foreach (ImplicitCallStep step in call.Steps)
                {
                    evidence = MergeEvidence(
                        evidence,
                        TraverseImplicitStep(member, call, step, rootType, operationId, active, lifecycle, workKind, inheritedWork, inheritedEffect, recoveryEffect));
                }
            }

            return evidence;
        }

        // A query expression calls the framework query operators with callbacks that run when the resulting sequence is
        // consumed, so the Work live at the query says nothing about them. The clause expressions are visited inline at
        // the query; one that can reach a producer site is refused as an opaque callback.
        private TraversalEvidence TraverseQueryExpression(AuthoredMember member, ImplicitCall call, string operationId)
        {
            if (call.Callbacks?.Any(callback => ContainsPotentialProducerSite(member with { Syntax = callback })) == true)
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new(
                    "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                    RootOperation(operationId) + "/query@" + Location(call.Anchor),
                    "query expression; Opaque callback semantics cannot prove execution or completion ownership."));
            }

            return TraversalEvidence.NoEvidence;
        }

        private TraversalEvidence TraverseImplicitStep(
            AuthoredMember member,
            ImplicitCall call,
            ImplicitCallStep step,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            GrimoireWorkKind? workKind,
            string? inheritedWork,
            string? inheritedEffect,
            string? recoveryEffect)
        {
            string? work = WorkAt(member, call.Anchor, operationId, workKind, inheritedWork, step.Position);

            string? effect = EffectAt(member, call.Anchor, operationId, inheritedEffect, step.Position);

            return TraverseImplicitTargets(
                member,
                call.Anchor,
                ResolveImplicitStep(member, step),
                step.Arguments,
                rootType,
                operationId,
                operationId + "/" + step.Label + "@" + Location(call.Anchor),
                active,
                lifecycle,
                work,
                effect,
                recoveryEffect);
        }

        // Traverses the members one implicit call runs under the frontiers live where the compiler runs them.
        private TraversalEvidence TraverseImplicitTargets(
            AuthoredMember member,
            SyntaxNode anchor,
            ImplicitTargets targets,
            IReadOnlyList<(ExpressionSyntax Expression, IParameterSymbol Parameter)> arguments,
            string rootType,
            string operationId,
            string site,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            string? work,
            string? effect,
            string? recoveryEffect)
        {
            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            foreach (string callee in targets.Unresolved.Distinct(StringComparer.Ordinal))
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(anchor), callee));
            }

            foreach (IMethodSymbol framework in targets.Framework)
            {
                string callee = Normalize(framework);

                if (Classify(framework, callee, anchor, member) is { } kind)
                {
                    evidence = MergeEvidence(evidence, AddSite(kind, callee, member, rootType, operationId, anchor, effect, work));
                }
                else if (!IsReviewedImplicitFrameworkMember(member, framework, anchor))
                {
                    RefuseTraversalCompletion();

                    diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(anchor), callee));
                }
            }

            foreach (AuthoredMember target in targets.Members)
            {
                AuthoredMember bound = arguments.Count != 0 && arguments.All(argument => argument.Expression.SyntaxTree == member.Model.SyntaxTree)
                    ? BindArguments(member, arguments.Select(static argument => (argument.Expression, (IParameterSymbol?)argument.Parameter, argument.Parameter.RefKind)), target)
                    : target;

                bool detached = IsAwaitable(target.Symbol.ReturnType) || IsLazySequence(target.Symbol.ReturnType);

                if (detached)
                {
                    RefuseTraversalCompletion();

                    diagnostics.Add(new(
                        "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN",
                        site,
                        Normalize(target.Symbol) + "; An awaitable or lazy implicit call has no proven completion within its caller's retained lifetime."));
                }

                evidence = MergeEvidence(
                    evidence,
                    Traverse(
                        bound,
                        rootType,
                        site,
                        active,
                        lifecycle,
                        detached ? null : work,
                        detached ? null : effect,
                        recoveryEffect: !detached && work is not null && effect is not null ? recoveryEffect : null));
            }

            return evidence;
        }

        // A constructor with a declaration of its own runs, before its body, the instance initializers of its type
        // and its base constructor, unless it chains to another constructor of the same type, which runs them instead.
        private TraversalEvidence TraverseConstructionPreamble(
            AuthoredMember member,
            ConstructorDeclarationSyntax declaration,
            string rootType,
            string operationId,
            HashSet<TraversalStateIdentity> active,
            bool lifecycle,
            GrimoireWorkKind? workKind,
            string? inheritedWork,
            string? inheritedEffect,
            string? recoveryEffect)
        {
            INamedTypeSymbol type = member.Symbol.ContainingType;

            if (declaration.Initializer?.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) == true
                || type.TypeKind != TypeKind.Class && !type.IsRecord)
            {
                return TraversalEvidence.NoEvidence;
            }

            ImplicitTargetCollector targets = new(this, member);

            targets.Members.AddRange(InitializerMembers(type, member.Symbol));

            if (declaration.Initializer is null && ImplicitBaseConstructor(type) is { } baseConstructor)
            {
                targets.Add(baseConstructor, baseConstructor);
            }

            if (targets.Members.Count == 0 && targets.Framework.Count == 0 && targets.Unresolved.Count == 0)
            {
                return TraversalEvidence.NoEvidence;
            }

            string? work = WorkAt(member, declaration, operationId, workKind, inheritedWork, declaration.SpanStart);

            string? effect = EffectAt(member, declaration, operationId, inheritedEffect, declaration.SpanStart);

            return TraverseImplicitTargets(
                member,
                declaration,
                targets.Result,
                [],
                rootType,
                operationId,
                operationId + "/construction@" + Location(declaration),
                active,
                lifecycle,
                work,
                effect,
                recoveryEffect);
        }

        // Whether an implicit call can reach a producer site, for the transitive scan an opaque callback body gets.
        private bool ImplicitCallReachesProducerSite(AuthoredMember member, ImplicitCall call, HashSet<GraphMemberIdentity> path)
        {
            if (call.RefusalCode is not null)
            {
                return true;
            }

            if (call.Kind == ImplicitCallKind.QueryClause)
            {
                return call.Callbacks?.Any(callback => ContainsPotentialProducerSite(member with { Syntax = callback }, path)) == true;
            }

            foreach (ImplicitCallStep step in call.Steps)
            {
                ImplicitTargets targets = ResolveImplicitStep(member, step);

                if (targets.Unresolved.Count != 0
                    || targets.Members.Any(target => ContainsPotentialProducerSite(target, path))
                    || targets.Framework.Any(framework => Classify(framework, Normalize(framework), call.Anchor, member) is not null || !IsReviewedImplicitFrameworkMember(member, framework, call.Anchor)))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
