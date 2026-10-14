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
        private static readonly string[] ExceptionVirtualMembers = ["Message", "StackTrace", "Data", "Source", "HelpLink"];

        private sealed class ImplicitTargetCollector(ProducerGraph graph, AuthoredMember member, HashSet<ISymbol>? shared = null)
        {
            // A collector made for a nested resolution shares the visited set of its parent, so that a generated member that
            // prints a value of its own type, directly or through a member typed object, is followed once.
            private readonly HashSet<ISymbol> visited = shared ?? new(SymbolEqualityComparer.Default);

            internal AuthoredMember Member => member;

            internal HashSet<ISymbol> Visited => visited;

            internal List<AuthoredMember> Members { get; } = [];

            internal List<IMethodSymbol> Framework { get; } = [];

            internal List<string> Unresolved { get; } = [];

            internal ImplicitTargets Result => new(Members, Framework, Unresolved);

            // The implementation of a formatting slot for a runtime type, and for an exception the virtual members that
            // Exception.ToString reads.
            internal void AddFormatting(INamedTypeSymbol type, IMethodSymbol slot)
            {
                ISymbol? implementation = EnumerationImplementation(type, slot);

                // A type that does not implement a format contract is not formatted through it.
                if (implementation is null && slot.ContainingType.TypeKind == TypeKind.Interface)
                {
                    return;
                }

                Add(implementation, slot);

                if (slot.Name != "ToString" || slot.Parameters.Length != 0)
                {
                    return;
                }

                for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
                {
                    if (TypeKey(current) != "System.Exception")
                    {
                        continue;
                    }

                    foreach (string name in ExceptionVirtualMembers)
                    {
                        if (current.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is { GetMethod: { } getter })
                        {
                            Add(EnumerationImplementation(type, getter), getter);
                        }
                    }
                }
            }

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

                // The base constructor of an EF context keeps its options and runs no EF service code: OnConfiguring
                // and OnModelCreating run at the first use of the context, which the first-use hooks traverse.
                if (method.MethodKind == MethodKind.Constructor
                    && IsExactFrameworkType(method.ContainingType, "Microsoft.EntityFrameworkCore.DbContext", typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName()))
                {
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

            // Under the Authored Enumerables Absent premise no authored type implements a framework collection
            // interface, so a call through one of them runs a framework implementation.
            if (candidates.Count == 0 && AuthoredEnumerablesAbsentByUse && IsPremiseCoveredEnumerationInterface(slot.ContainingType))
            {
                targets.Add(slot, slot);

                return targets.Result;
            }

            // A value formatted by an interpolation or a concatenation whose runtime type nothing proves can be any
            // authored type that formats with code of its own; the framework types it can be run no authored code,
            // except through such an authored subclass.
            if (candidates.Count == 0 && step.Label is "to-string" or "format" && receiverType is not null)
            {
                AddFormatterCandidates(receiverType, slot, targets);

                return targets.Result;
            }

            if (candidates.Count == 0)
            {
                targets.Unresolved.Add(Normalize(slot));

                return targets.Result;
            }

            foreach (INamedTypeSymbol candidate in candidates)
            {
                if (step.Label is "to-string" or "format")
                {
                    targets.AddFormatting(candidate, slot);
                }
                else
                {
                    targets.Add(EnumerationImplementation(candidate, slot), slot);
                }
            }

            return targets.Result;
        }

        // An authored class or struct that formats with code of its own, with the keys of every type it is assignable
        // to (itself, its base types and its interfaces) and the implementations that do the formatting.
        private sealed record AuthoredFormatter(
            INamedTypeSymbol Type,
            IReadOnlySet<string> AssignableKeys,
            IReadOnlyList<(IMethodSymbol Slot, ISymbol Implementation)> Implementations);

        private IReadOnlyList<AuthoredFormatter>? authoredFormatters;

        private IReadOnlyList<AuthoredFormatter> AuthoredFormatters() => authoredFormatters ??= BuildAuthoredFormatters();

        private IReadOnlyList<AuthoredFormatter> BuildAuthoredFormatters()
        {
            List<AuthoredFormatter> formatters = [];

            if (analysedCompilations.Count == 0)
            {
                return formatters;
            }

            IMethodSymbol? toString = analysedCompilations[0].GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(static method => method.Parameters.Length == 0);

            foreach (INamedTypeSymbol type in analysedCompilations.SelectMany(static compilation => SourceTypes(compilation.Assembly.GlobalNamespace)))
            {
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract || type.IsStatic || type.IsImplicitlyDeclared || !type.Locations.Any(static location => location.IsInSource))
                {
                    continue;
                }

                List<IMethodSymbol> slots = [];

                if (toString is not null)
                {
                    slots.Add(toString);
                }

                foreach (INamedTypeSymbol contract in type.AllInterfaces)
                {
                    if (TypeKey(contract) is "System.IFormattable" or "System.ISpanFormattable" or "System.IUtf8SpanFormattable"
                        && contract.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(static method => method.Name is "ToString" or "TryFormat") is { } contractSlot)
                    {
                        slots.Add(contractSlot);
                    }
                }

                for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
                {
                    if (TypeKey(current) != "System.Exception")
                    {
                        continue;
                    }

                    foreach (string name in ExceptionVirtualMembers)
                    {
                        if (current.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is { GetMethod: { } getter })
                        {
                            slots.Add(getter);
                        }
                    }
                }

                List<(IMethodSymbol Slot, ISymbol Implementation)> implementations = [];

                foreach (IMethodSymbol slot in slots)
                {
                    if (EnumerationImplementation(type, slot) is { } implementation
                        && (implementation.Locations.Any(static location => location.IsInSource) || implementation.IsImplicitlyDeclared && implementation.ContainingType.Locations.Any(static location => location.IsInSource)))
                    {
                        implementations.Add((slot, implementation));
                    }
                }

                if (implementations.Count == 0)
                {
                    continue;
                }

                formatters.Add(new(type, AssignableKeys(type), implementations));
            }

            return formatters;
        }

        // The authored formatters that can be a runtime value of the static type, with the implementation of the slot
        // each runs. A formatter that cannot reach a producer site adds nothing, so it is not traversed.
        private void AddFormatterCandidates(ITypeSymbol receiverType, IMethodSymbol slot, ImplicitTargetCollector targets)
        {
            ImplicitTargetCollector formatting = new(this, targets.Member, targets.Visited);

            foreach (AuthoredFormatter formatter in AuthoredFormatters())
            {
                if (!IsFormatterAssignableTo(formatter, receiverType))
                {
                    continue;
                }

                formatting.AddFormatting(formatter.Type, slot);
            }

            targets.Members.AddRange(formatting.Members.Where(ContainsPotentialProducerSite));

            targets.Framework.AddRange(formatting.Framework);

            targets.Unresolved.AddRange(formatting.Unresolved);
        }

        // The display names of every type a type is assignable to: itself, its base types and its interfaces. The names are
        // compared instead of the symbols because the symbols of different compilations are never equal.
        private static IReadOnlySet<string> AssignableKeys(INamedTypeSymbol type)
        {
            HashSet<string> keys = new(StringComparer.Ordinal) { type.OriginalDefinition.ToDisplayString() };

            for (INamedTypeSymbol? baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                keys.Add(baseType.OriginalDefinition.ToDisplayString());
            }

            foreach (INamedTypeSymbol contract in type.AllInterfaces)
            {
                keys.Add(contract.OriginalDefinition.ToDisplayString());
            }

            return keys;
        }

        private static bool IsFormatterAssignableTo(AuthoredFormatter formatter, ITypeSymbol receiver) =>
            receiver.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType
            || receiver.TypeKind == TypeKind.TypeParameter
            || formatter.AssignableKeys.Contains(receiver.OriginalDefinition.ToDisplayString());

        // The construction a constructor without a declaration of its own runs: the instance initializers of its
        // type, then its base constructor. A constructor declared with a body gets the same preamble when it is traversed.
        private void AddConstructionEntry(AuthoredMember member, IMethodSymbol constructor, ImplicitTargetCollector targets)
        {
            INamedTypeSymbol type = constructor.ContainingType;

            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
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
            if (type.TypeKind != TypeKind.Class || type.BaseType is not { SpecialType: not SpecialType.System_Object } baseType)
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
        private readonly Dictionary<INamedTypeSymbol, IReadOnlyList<AuthoredMember>> initializerMembers = new(SymbolEqualityComparer.Default);

        private IReadOnlyList<AuthoredMember> InitializerMembers(INamedTypeSymbol type, IMethodSymbol constructor)
        {
            if (initializerMembers.TryGetValue(type, out IReadOnlyList<AuthoredMember>? known))
            {
                return known;
            }

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

            initializerMembers[type] = initializers;

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

            IMethodSymbol? toString = member.Model.Compilation.GetSpecialType(SpecialType.System_Object).GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(static method => method.Parameters.Length == 0);

            if (toString is null)
            {
                return;
            }

            if (!IsSealedOrValue(type))
            {
                AddFormatterCandidates(type, toString, targets);

                return;
            }

            targets.AddFormatting((INamedTypeSymbol)type, toString);
        }

        private bool IsReviewedImplicitFrameworkMember(AuthoredMember member, IMethodSymbol method, SyntaxNode anchor) =>
            IsReviewedFrameworkAwaiterMember(method)
            || IsReviewedFrameworkDeconstruct(method)
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

        // The parameterless constructors of the framework's in-memory synchronization and weak-table types allocate
        // and run nothing else. A constructor that takes a callback is not among them.
        private static bool IsReviewedExactInMemoryConstruction(IMethodSymbol method) =>
            method is { MethodKind: MethodKind.Constructor, Parameters.Length: 0 }
            && method.ContainingType is { } type
            && (IsExactFrameworkType(type, "System.Threading.Lock", typeof(System.Threading.Lock).Assembly.GetName())
                || IsExactFrameworkType(type, "System.Threading.AsyncLocal`1", typeof(AsyncLocal<>).Assembly.GetName())
                || IsExactFrameworkType(type, "System.Runtime.CompilerServices.ConditionalWeakTable`2", typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>).Assembly.GetName()));

        // The Deconstruct members of the framework's pair and tuple types copy their fields into the out arguments.
        private static bool IsReviewedFrameworkDeconstruct(IMethodSymbol method) =>
            method.Name == "Deconstruct"
            && method.ContainingType is { } type
            && (IsExactFrameworkType(type, "System.Collections.Generic.KeyValuePair`2", typeof(KeyValuePair<,>).Assembly.GetName())
                || IsExactFrameworkType(type, "System.Collections.DictionaryEntry", typeof(System.Collections.DictionaryEntry).Assembly.GetName())
                || IsExactFrameworkType(type, "System.TupleExtensions", typeof(TupleExtensions).Assembly.GetName()));

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
                || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
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

        // The implicit calls the syntax lanes traverse (property accessors, user-defined conversions and the cleanup of
        // a using statement or declaration), for the transitive scan: an opaque callback body is judged by everything
        // the traversal would follow in it, not only by the calls written there.
        private bool LaneImplicitCallsReachProducerSite(AuthoredMember member, SyntaxNode node, HashSet<GraphMemberIdentity> path)
        {
            Compilation compilation = member.Model.Compilation;

            bool Reaches(IMethodSymbol method, SyntaxNode anchor) =>
                Resolve(method, compilation) is { } target
                    ? target.Syntax is not AccessorDeclarationSyntax { Body: null, ExpressionBody: null } && ContainsPotentialProducerSite(target, path)
                    : RequiresExternalBoundaryClassification(method, compilation, anchor, member.Model, member);

            foreach (IConversionOperation conversion in UserConversionsAt(member, node))
            {
                if (Reaches(conversion.OperatorMethod!, conversion.Syntax))
                {
                    return true;
                }
            }

            if (node is IdentifierNameSyntax or MemberAccessExpressionSyntax or ElementAccessExpressionSyntax or ElementBindingExpressionSyntax or ImplicitElementAccessSyntax
                && member.Model.GetSymbolInfo(node).Symbol is IPropertySymbol property)
            {
                foreach (IMethodSymbol accessor in new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>())
                {
                    if (Resolve(accessor, compilation) is { } target
                        && target.Syntax is not AccessorDeclarationSyntax { Body: null, ExpressionBody: null }
                        && ContainsPotentialProducerSite(target, path))
                    {
                        return true;
                    }
                }
            }

            bool CleanupReaches(ITypeSymbol? type, ExpressionSyntax? resource, bool asynchronous)
            {
                if (type is null)
                {
                    return true;
                }

                if (type.ToDisplayString() is "System.IO.StreamWriter" or "RetroDownfall.Arcanum.Core.Storage.EncryptedBlobWriter")
                {
                    return true;
                }

                EffectiveCleanupResolution cleanup = ResolveEffectiveCleanup(member, type, resource, asynchronous ? "DisposeAsync" : "Dispose");

                return cleanup.ExternalEffects.Count != 0
                    || cleanup.Targets.Any(target => ContainsPotentialProducerSite(target, path))
                    || cleanup.Targets.Count == 0 && cleanup.MustResolve;
            }

            switch (node)
            {
                case UsingStatementSyntax { Expression: { } resource } usingStatement:
                    return CleanupReaches(member.Model.GetTypeInfo(resource).Type, resource, usingStatement.AwaitKeyword.RawKind != 0);

                case VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } or UsingStatementSyntax } declaration:
                    {
                        bool asynchronous = declaration.Parent is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: not 0 } or UsingStatementSyntax { AwaitKeyword.RawKind: not 0 };

                        ITypeSymbol? type = member.Model.GetTypeInfo(declaration.Type).Type;

                        return declaration.Variables.Any(variable => CleanupReaches(type, variable.Initializer?.Value, asynchronous));
                    }

                default:
                    return false;
            }
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
