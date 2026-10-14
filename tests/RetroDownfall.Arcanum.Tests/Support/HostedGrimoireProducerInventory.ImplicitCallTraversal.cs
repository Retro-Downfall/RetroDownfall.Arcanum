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

        // The virtual properties that the framework ToString of a base type of the type reads, whose implementations on the
        // type are authored code that formatting the type runs: Exception.ToString reads Message, StackTrace, Data, Source
        // and HelpLink, and AITool.ToString returns Name.
        private static IEnumerable<IMethodSymbol> FrameworkToStringReads(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (TypeKey(current) == "System.Exception")
                {
                    foreach (string name in ExceptionVirtualMembers)
                    {
                        if (current.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is { GetMethod: { } getter })
                        {
                            yield return getter;
                        }
                    }
                }
                else if (IsExactAiTool(current) && current.GetMembers("Name").OfType<IPropertySymbol>().FirstOrDefault() is { GetMethod: { } nameGetter })
                {
                    yield return nameGetter;
                }
            }
        }

        private static bool IsExactAiTool(ITypeSymbol type) =>
            IsExactFrameworkType(type, "Microsoft.Extensions.AI.AITool", typeof(Microsoft.Extensions.AI.AITool).Assembly.GetName());

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
                // The members of an anonymous type are generated, and Roslyn does not expose them: the type formats,
                // compares and hashes every member it holds.
                if (type.IsAnonymousType && slot.ContainingType.SpecialType == SpecialType.System_Object)
                {
                    graph.AddSynthesizedByName(member, type, slot.Name, slot, this);

                    return;
                }

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

                bool readsInnerExceptions = false;

                foreach (IMethodSymbol getter in FrameworkToStringReads(type))
                {
                    Add(EnumerationImplementation(type, getter), getter);

                    readsInnerExceptions |= getter.ContainingType.Name == "Exception";
                }

                // Exception.ToString formats the inner exceptions too (every one of an AggregateException), and an inner
                // exception can be any exception the universe declares, so the formatting of each of them is followed.
                if (readsInnerExceptions
                    && member.Model.Compilation.GetTypeByMetadataName("System.Exception") is { } exception
                    && visited.Add(exception))
                {
                    graph.AddFormatterCandidates(exception, slot, this);
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
                    // A constructor declared with a body of its own that did not resolve to an authored member is not the
                    // implicit or primary constructor the construction entry stands for.
                    if (method.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is ConstructorDeclarationSyntax))
                    {
                        Unresolved.Add(Normalize(method));

                        return;
                    }

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
            if (candidates.Count == 0 && step.Label is "to-string" or "format" or "callback" && receiverType is not null)
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
                else if (candidate.IsAnonymousType && slot.ContainingType.SpecialType == SpecialType.System_Object)
                {
                    AddSynthesizedByName(member, candidate, slot.Name, slot, targets);
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
            IReadOnlyList<(IMethodSymbol Slot, ISymbol Implementation)> Implementations,
            bool ReadsForToString);

        // The framework contracts whose members a framework member calls on a value it is handed: it compares, hashes,
        // orders and formats the value, and asks a format provider for its formatter.
        private static readonly IReadOnlySet<string> CallbackContracts = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.IEquatable`1",
            "System.IComparable",
            "System.IComparable`1",
            "System.Collections.IEqualityComparer",
            "System.Collections.Generic.IEqualityComparer`1",
            "System.Collections.IComparer",
            "System.Collections.Generic.IComparer`1",
            "System.IFormatProvider",
            "System.ICustomFormatter",
            "System.Runtime.CompilerServices.ITuple",
        };

        private IReadOnlyList<AuthoredFormatter>? authoredFormatters;

        private IReadOnlyList<AuthoredFormatter> AuthoredFormatters() => authoredFormatters ??= BuildAuthoredFormatters();

        private IReadOnlyList<AuthoredFormatter> BuildAuthoredFormatters()
        {
            List<AuthoredFormatter> formatters = [];

            if (analysedCompilations.Count == 0)
            {
                return formatters;
            }

            INamedTypeSymbol objectType = analysedCompilations[0].GetSpecialType(SpecialType.System_Object);

            IMethodSymbol? toString = objectType.GetMembers("ToString").OfType<IMethodSymbol>().FirstOrDefault(static method => method.Parameters.Length == 0);

            IMethodSymbol[] objectCallbackSlots =
            [
                .. objectType.GetMembers("Equals").OfType<IMethodSymbol>().Where(static method => !method.IsStatic && method.Parameters.Length == 1),
                .. objectType.GetMembers("GetHashCode").OfType<IMethodSymbol>().Where(static method => method.Parameters.Length == 0),
            ];

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

                // The equality and hash members of object are called on a value a framework member compares or hashes.
                foreach (IMethodSymbol objectSlot in objectCallbackSlots)
                {
                    slots.Add(objectSlot);
                }

                foreach (INamedTypeSymbol contract in type.AllInterfaces)
                {
                    if (TypeKey(contract) is "System.IFormattable" or "System.ISpanFormattable" or "System.IUtf8SpanFormattable"
                        && contract.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(static method => method.Name is "ToString" or "TryFormat") is { } contractSlot)
                    {
                        slots.Add(contractSlot);
                    }
                    else if (CallbackContracts.Contains(TypeKey(contract)))
                    {
                        slots.AddRange(contract.GetMembers().OfType<IMethodSymbol>().Where(static method => method.MethodKind is MethodKind.Ordinary or MethodKind.PropertyGet && !method.IsStatic));
                    }
                }

                slots.AddRange(FrameworkToStringReads(type));

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

                // ToString of a framework base type (an exception, a tool) also reads the virtual members the type overrides.
                bool readsForToString = FrameworkToStringReads(type).Any(getter => EnumerationImplementation(type, getter) is { } read
                    && (read.Locations.Any(static location => location.IsInSource) || read.IsImplicitlyDeclared && read.ContainingType.Locations.Any(static location => location.IsInSource)));

                formatters.Add(new(type, AssignableKeys(type), implementations, readsForToString));
            }

            return formatters;
        }

        // The authored formatters that can be a runtime value of the static type, with the implementation of the slot
        // each runs. A formatter that cannot reach a producer site adds nothing, so it is not traversed.
        private void AddFormatterCandidates(ITypeSymbol receiverType, IMethodSymbol slot, ImplicitTargetCollector targets)
        {
            ImplicitTargetCollector formatting = new(this, targets.Member, targets.Visited);

            // The framework implementation the static type itself runs when the value is exactly that type: a framework
            // class or struct formats, compares and hashes with code of its own, which is classified or refused as it is for
            // a value whose type is proved.
            if (receiverType is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } staticType
                && !staticType.Locations.Any(static location => location.IsInSource)
                && staticType.SpecialType != SpecialType.System_Object)
            {
                formatting.AddFormatting(staticType, slot);
            }

            foreach (AuthoredFormatter formatter in AuthoredFormatters())
            {
                // ToString of a framework base type also reads the virtual properties its subclass overrides (the members of an
                // exception, the name of a tool), so a formatter with such an override is a candidate for ToString.
                bool readsForToString = slot.Name == "ToString" && slot.Parameters.Length == 0 && formatter.ReadsForToString;

                if (!IsFormatterAssignableTo(formatter, receiverType)
                    || !readsForToString && !formatter.Implementations.Any(implementation => SameEnumerationSlot(implementation.Slot, slot)))
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

            // An initializer that creates its own type asks for this list while it is being built; that request ends here.
            initializerMembers[type] = initializers;

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
        private void AddSynthesizedMember(AuthoredMember member, IMethodSymbol synthesized, ImplicitTargetCollector targets) =>
            AddSynthesizedByName(member, synthesized.ContainingType, synthesized.Name, synthesized, targets);

        // The members one generated member of a record or an anonymous type runs, named by the generated member (Roslyn does
        // not expose the generated members of an anonymous type, so the name comes from the object member it overrides).
        private void AddSynthesizedByName(AuthoredMember member, INamedTypeSymbol record, string name, IMethodSymbol slot, ImplicitTargetCollector targets)
        {
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

            switch (name)
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
                        targets.Unresolved.Add(Normalize(slot));
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

                            // The accessor of an anonymous type is generated and reads its field.
                            if (!current.IsAnonymousType && printed is IPropertySymbol { GetMethod: { } getter })
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

                // Equality and the hash compare and hash every field the type holds (every property of an anonymous type)
                // with the default comparer of the field's type, which calls the Equals and GetHashCode that type declares.
                case "Equals" or "GetHashCode":
                    for (INamedTypeSymbol? current = record; current is not null && (current.IsRecord || current.IsAnonymousType); current = current.BaseType)
                    {
                        IEnumerable<ITypeSymbol> compared = current.IsAnonymousType
                            ? current.GetMembers().OfType<IPropertySymbol>().Where(static property => !property.IsStatic && !property.IsIndexer).Select(static property => property.Type)
                            : current.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic).Select(static field => field.Type);

                        foreach (ITypeSymbol comparedType in compared)
                        {
                            AddEquality(member, comparedType, targets, 0);
                        }
                    }

                    break;

                default:
                    targets.Unresolved.Add(Normalize(slot));

                    break;
            }
        }

        // The members the default comparer of a field type calls when a generated Equals or GetHashCode compares or hashes
        // the field: the Equals(object), IEquatable<T>.Equals and GetHashCode the type declares. The framework value types
        // and primitives compare with no authored code; a nullable value compares the value it holds, a tuple each of its
        // elements; a type that is not exact adds the members of every authored type that can be the field.
        private void AddEquality(AuthoredMember member, ITypeSymbol type, ImplicitTargetCollector targets, int depth)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
            }

            if (IsIntrinsicFormatType(type) || type.TypeKind is TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Delegate or TypeKind.Array || type is IArrayTypeSymbol)
            {
                return;
            }

            if (depth < 4 && type is INamedTypeSymbol { IsTupleType: true } tuple)
            {
                foreach (IFieldSymbol element in tuple.TupleElements)
                {
                    AddEquality(member, element.Type, targets, depth + 1);
                }

                return;
            }

            INamedTypeSymbol objectType = member.Model.Compilation.GetSpecialType(SpecialType.System_Object);

            foreach (IMethodSymbol slot in objectType.GetMembers().OfType<IMethodSymbol>().Where(static method => !method.IsStatic && (method.Name == "GetHashCode" && method.Parameters.Length == 0 || method.Name == "Equals" && method.Parameters.Length == 1)))
            {
                if (IsSealedOrValue(type))
                {
                    targets.Add(EnumerationImplementation(type, slot), slot);
                }
                else
                {
                    AddFormatterCandidates(type, slot, targets);
                }
            }

            if (type is INamedTypeSymbol exact && IsSealedOrValue(type))
            {
                foreach (INamedTypeSymbol contract in exact.AllInterfaces.Where(static contract => TypeKey(contract) == "System.IEquatable`1"))
                {
                    foreach (IMethodSymbol slot in contract.GetMembers("Equals").OfType<IMethodSymbol>())
                    {
                        targets.Add(EnumerationImplementation(exact, slot), slot);
                    }
                }
            }
            else if (type is INamedTypeSymbol open)
            {
                foreach (INamedTypeSymbol contract in open.AllInterfaces.Where(static contract => TypeKey(contract) == "System.IEquatable`1"))
                {
                    foreach (IMethodSymbol slot in contract.GetMembers("Equals").OfType<IMethodSymbol>())
                    {
                        AddFormatterCandidates(open, slot, targets);
                    }
                }
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
        // type that is not exact adds those of every authored formatter assignable to it.
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
            || IsAiToolToStringWithNameModeled(method)
            || IsReviewedFrameworkUriFormatting(method)
            || !RequiresExternalBoundaryClassification(method, member.Model.Compilation, anchor, member.Model, member);

        // System.Uri is not sealed, so a hole typed Uri can hold an authored subclass whose ToString is followed as an
        // authored formatter. The framework class itself formats its stored text and runs no supplied code, which is
        // reviewed for the exact framework type only.
        private static bool IsReviewedFrameworkUriFormatting(IMethodSymbol method) =>
            method.ContainingType is { } type
            && IsExactFrameworkType(type, "System.Uri", typeof(Uri).Assembly.GetName())
            && (method.Name is "ToString" or "TryFormat" || method.Name.EndsWith(".ToString", StringComparison.Ordinal) || method.Name.EndsWith(".TryFormat", StringComparison.Ordinal));

        // AITool.ToString returns the virtual Name of the tool and nothing else. It is reviewed for an implicit formatting
        // call only, because the formatting resolution adds the Name implementation of the exact tool type; a written
        // call does not model Name and stays unreviewed.
        private static bool IsAiToolToStringWithNameModeled(IMethodSymbol method) =>
            method is { Name: "ToString", IsStatic: false, Parameters.Length: 0 }
            && IsExactAiTool(method.ContainingType);

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
            // The holes of a string that is formatted when it is consumed run at a point nothing here places, so they are
            // traversed under no frontier at all.
            bool deferred = call.Kind == ImplicitCallKind.DeferredFormatHole;

            string? work = deferred ? null : WorkAt(member, call.Anchor, operationId, workKind, inheritedWork, step.Position);

            string? effect = deferred ? null : EffectAt(member, call.Anchor, operationId, inheritedEffect, step.Position);

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
                recoveryEffect,
                step.Values);
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
            string? recoveryEffect,
            IReadOnlyList<CallbackValue>? values = null)
        {
            TraversalEvidence evidence = TraversalEvidence.NoEvidence;

            foreach (string callee in targets.Unresolved.Distinct(StringComparer.Ordinal))
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(anchor), callee));
            }

            // A framework member calls back into the values it is handed.
            if (targets.Framework.Count != 0 && values is { Count: > 0 } && FrameworkValuesRunAuthoredCallbacks(member, values))
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new("HOSTED_CALLBACK_OWNERSHIP_UNPROVEN", site, Normalize(targets.Framework[0]) + CallbackDetail));
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

        // The construction a declared constructor runs before its body (instance initializers, base constructor), for the
        // transitive scan. An initializer is listed only when it can reach a producer site.
        private bool ConstructionPreambleReachesProducerSite(AuthoredMember member, HashSet<GraphMemberIdentity> path)
        {
            if (member.Symbol is not { MethodKind: MethodKind.Constructor, IsStatic: false }
                || member.Syntax is not ConstructorDeclarationSyntax declaration
                || declaration.Initializer?.ThisOrBaseKeyword.IsKind(SyntaxKind.ThisKeyword) == true
                || member.Symbol.ContainingType is not { TypeKind: TypeKind.Class or TypeKind.Struct } type)
            {
                return false;
            }

            if (InitializerMembers(type, member.Symbol).Count != 0)
            {
                return true;
            }

            if (declaration.Initializer is not null || ImplicitBaseConstructor(type) is not { } baseConstructor)
            {
                return false;
            }

            ImplicitTargetCollector targets = new(this, member);

            targets.Add(baseConstructor, baseConstructor);

            return ImplicitTargetsReachProducerSite(member, targets.Result, declaration, path);
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
                    : !scanSitesOnly && RequiresExternalBoundaryClassification(method, compilation, anchor, member.Model, member);

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
                (ITypeSymbol? receiverType, ExpressionSyntax? receiverExpression, bool exactReceiver) = AccessorReceiver((member.Model.GetOperation(node) as IPropertyReferenceOperation)?.Instance);

                foreach (IMethodSymbol accessor in new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>())
                {
                    AccessorDispatch dispatch = ResolveAccessorDispatch(member, accessor, receiverType, receiverExpression, exactReceiver, property.IsIndexer);

                    if (!scanSitesOnly && dispatch.Unresolved || dispatch.Targets.Any(target => ContainsPotentialProducerSite(target, path)))
                    {
                        return true;
                    }
                }
            }

            if (node is IdentifierNameSyntax or MemberAccessExpressionSyntax
                && node.Parent is AssignmentExpressionSyntax { Left: { } eventTarget } eventMutation
                && eventTarget == node
                && eventMutation.Kind() is SyntaxKind.AddAssignmentExpression or SyntaxKind.SubtractAssignmentExpression
                && member.Model.GetSymbolInfo(node).Symbol is IEventSymbol { } mutatedEvent
                && mutatedEvent.Locations.Any(static location => location.IsInSource)
                && (eventMutation.IsKind(SyntaxKind.AddAssignmentExpression) ? mutatedEvent.AddMethod : mutatedEvent.RemoveMethod) is { } eventAccessor)
            {
                (ITypeSymbol? receiverType, ExpressionSyntax? receiverExpression, bool exactReceiver) = AccessorReceiver((member.Model.GetOperation(eventMutation) as IEventAssignmentOperation)?.EventReference is IEventReferenceOperation eventReference ? eventReference.Instance : null);

                AccessorDispatch dispatch = ResolveAccessorDispatch(member, eventAccessor, receiverType, receiverExpression, exactReceiver, indexer: false);

                if (!scanSitesOnly && dispatch.Unresolved || dispatch.Targets.Any(target => ContainsPotentialProducerSite(target, path)))
                {
                    return true;
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
                    || !scanSitesOnly && cleanup.Targets.Count == 0 && cleanup.MustResolve;
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

        private const string CallbackDetail = "; Opaque callback semantics of a value handed to a framework member (ToString, Equals, GetHashCode, CompareTo, GetFormat) cannot prove execution or completion ownership.";

        // The types a framework member can reach authored callbacks through when it is handed a value of the type: the
        // type itself, or the element of an array or a nullable value.
        private static ITypeSymbol CallbackCarrier(ITypeSymbol type)
        {
            while (true)
            {
                if (type is IArrayTypeSymbol array)
                {
                    type = array.ElementType;
                }
                else if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                {
                    type = nullable.TypeArguments[0];
                }
                else
                {
                    return type;
                }
            }
        }

        private static CallbackFamilies FamilyOf(IMethodSymbol slot)
        {
            if (slot.MethodKind == MethodKind.PropertyGet)
            {
                return slot.ContainingType.Name is "Exception" or "AITool" ? CallbackFamilies.Format : CallbackFamilies.None;
            }

            if (slot.ContainingType.SpecialType == SpecialType.System_Object)
            {
                return slot.Name == "ToString" ? CallbackFamilies.Format : CallbackFamilies.Equality;
            }

            return TypeKey(slot.ContainingType) switch
            {
                "System.IFormattable" or "System.ISpanFormattable" or "System.IUtf8SpanFormattable" => CallbackFamilies.Format,
                "System.IEquatable`1" => CallbackFamilies.Equality,
                "System.IComparable" or "System.IComparable`1" => CallbackFamilies.Compare,
                "System.IFormatProvider" or "System.ICustomFormatter" => CallbackFamilies.Provider,
                "System.Collections.IComparer" or "System.Collections.Generic.IComparer`1" or "System.Collections.IEqualityComparer" or "System.Collections.Generic.IEqualityComparer`1" => CallbackFamilies.Comparer,
                _ => CallbackFamilies.None,
            };
        }

        // The authored implementers of the framework slots (the ToString, Equals, GetHashCode, CompareTo, GetFormat and
        // Format members) of the families a value of the type can have, judged by the scan: whether any of them reaches a
        // producer site or cannot be followed. A result that was cut by a cycle is not remembered.
        private readonly Dictionary<(string Type, CallbackFamilies Families), bool> callbackImplementerReach = [];

        private readonly HashSet<(string Type, CallbackFamilies Families)> callbackImplementerReachInProgress = [];

        private bool CallbackImplementersReach(AuthoredMember member, ITypeSymbol type, CallbackFamilies families)
        {
            if (type.TypeKind is TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Delegate or TypeKind.Enum
                || type.SpecialType == SpecialType.System_String
                || IsIntrinsicFormatType(type))
            {
                return false;
            }

            (string, CallbackFamilies) key = (type.ToDisplayString(), families);

            if (callbackImplementerReach.TryGetValue(key, out bool known))
            {
                return known;
            }

            if (!callbackImplementerReachInProgress.Add(key))
            {
                producerSiteScanCuts++;

                return false;
            }

            int cutsBefore = producerSiteScanCuts;

            bool reaches = false;

            try
            {
                foreach (AuthoredFormatter implementer in AuthoredFormatters())
                {
                    if (!IsFormatterAssignableTo(implementer, type))
                    {
                        continue;
                    }

                    foreach ((IMethodSymbol slot, ISymbol implementation) in implementer.Implementations)
                    {
                        if ((FamilyOf(slot) & families) == CallbackFamilies.None)
                        {
                            continue;
                        }

                        ImplicitTargetCollector collector = new(this, member);

                        collector.Add(implementation, slot);

                        if (ImplicitTargetsReachProducerSite(member, collector.Result, member.Syntax, []))
                        {
                            reaches = true;

                            break;
                        }
                    }

                    if (reaches)
                    {
                        break;
                    }
                }
            }
            finally
            {
                callbackImplementerReachInProgress.Remove(key);
            }

            if (producerSiteScanCuts == cutsBefore)
            {
                callbackImplementerReach[key] = reaches;
            }

            return reaches;
        }

        // The authored implementers of the framework slots that reach a producer site or cannot be followed, with the slot and
        // what was found, for the pin that names them.
        internal IReadOnlyList<string> CallbackImplementersThatReach()
        {
            List<string> found = [];

            if (analysedCompilations.Count == 0)
            {
                return found;
            }

            AuthoredMember anchor = AuthoredMembers.First();

            foreach (AuthoredFormatter implementer in AuthoredFormatters())
            {
                foreach ((IMethodSymbol slot, ISymbol implementation) in implementer.Implementations)
                {
                    ImplicitTargetCollector collector = new(this, anchor);

                    collector.Add(implementation, slot);

                    string reason = !ImplicitTargetsReachProducerSite(anchor, collector.Result, anchor.Syntax, [])
                        ? string.Empty
                        : collector.Unresolved.Count != 0 && !scanSitesOnly
                            ? "unresolved " + string.Join(",", collector.Unresolved.Distinct())
                            : collector.Members.FirstOrDefault(target => ContainsPotentialProducerSite(target)) is { } reaching
                                ? "body " + Normalize(reaching.Symbol)
                                : "framework " + string.Join(",", collector.Framework.Select(Normalize));

                    if (reason.Length != 0)
                    {
                        found.Add(TypeKey(implementer.Type) + " :: " + Normalize(slot) + " :: " + reason);
                    }
                }
            }

            return found;
        }

        // The graph that asks about classified producer sites alone, one per compilation, shared by every graph over it. What
        // runs at a point nothing here places (a callback a framework member calls, a static initializer, one of several
        // bodies a receiver might select) is judged by whether it can reach a site, not by whether it uses a framework
        // member nobody has reviewed: no traversal enters it, so there is no diagnostic for the second to be the cause of.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CSharpCompilation, Lazy<ProducerGraph>> sitesOnlyGraphs = new();

        private ProducerGraph SitesOnlyGraph()
        {
            if (scanSitesOnly)
            {
                return this;
            }

            IReadOnlyList<CSharpCompilation> compilations = analysedCompilations;

            return sitesOnlyGraphs.GetValue(
                compilations[0],
                _ => new Lazy<ProducerGraph>(
                    () => new ProducerGraph(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth, DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false, sitesOnly: true),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        // Whether the member can reach a classified producer site. A member the sites-only graph has no model for cannot be
        // judged, so it reaches one.
        private bool SitesOnlyReaches(AuthoredMember member)
        {
            ProducerGraph sites = SitesOnlyGraph();

            lock (sites)
            {
                if (!sites.semanticModels.TryGetValue(member.Syntax.SyntaxTree, out SemanticModel? model))
                {
                    return true;
                }

                return sites.ContainsPotentialProducerSite(new AuthoredMember(member.Symbol, member.Syntax, model));
            }
        }

        private bool FrameworkValuesRunAuthoredCallbacks(AuthoredMember member, IEnumerable<CallbackValue> values)
        {
            ProducerGraph sites = SitesOnlyGraph();

            lock (sites)
            {
                AuthoredMember anchor = sites.AuthoredMembers.First();

                return values.Any(value => sites.CallbackImplementersReach(anchor, CallbackCarrier(value.Type), value.Families));
            }
        }

        // A framework member handed a value calls the ToString, Equals, GetHashCode, CompareTo or GetFormat of its runtime
        // type: when it formats it, compares it, hashes it or asks it (a format provider) for a formatter. The families the
        // parameter's declared type allows are judged for every authored type the value can be, by the transitive scan, and
        // the call is refused when one of them reaches a producer site: the member runs them at a point of its own, which
        // nothing here places.
        private bool FrameworkMemberRunsAuthoredCallback(AuthoredMember member, SyntaxNode node, IMethodSymbol method, out string detail)
        {
            detail = Normalize(method) + CallbackDetail;

            IEnumerable<IArgumentOperation> arguments = member.Model.GetOperation(node) switch
            {
                IInvocationOperation invocation => invocation.Arguments,
                IObjectCreationOperation creation => creation.Arguments,
                _ => [],
            };

            return FrameworkValuesRunAuthoredCallbacks(member, ArgumentValues(arguments, member.Model.GetOperation(node)));
        }

        // An authored class or struct a value can be at run time, with the keys of every type it is assignable to.
        private sealed record AuthoredInstantiable(INamedTypeSymbol Type, IReadOnlySet<string> AssignableKeys);

        private IReadOnlyList<AuthoredInstantiable>? authoredInstantiables;

        private IReadOnlyList<AuthoredInstantiable> AuthoredInstantiables() => authoredInstantiables ??=
        [
            .. analysedCompilations
                .SelectMany(static compilation => SourceTypes(compilation.Assembly.GlobalNamespace))
                .Where(static type => type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsAbstract && !type.IsStatic && !type.IsImplicitlyDeclared && type.Locations.Any(static location => location.IsInSource))
                .Select(static type => new AuthoredInstantiable(type, AssignableKeys(type))),
        ];

        // The authored bodies a property or an event accessor runs, and whether a body that cannot be followed may run.
        private sealed record AccessorDispatch(IReadOnlyList<AuthoredMember> Targets, bool Unresolved);

        // The accessor an access calls is a slot the runtime type of the receiver selects when it is virtual, abstract, an
        // override that can be overridden again, or an interface member; any other accessor is its own implementation.
        private static bool IsDispatchedAccessor(IMethodSymbol accessor) =>
            !accessor.IsStatic
            && (accessor.ContainingType.TypeKind == TypeKind.Interface
                || (accessor.IsVirtual || accessor.IsAbstract || accessor.IsOverride) && !accessor.IsSealed);

        private readonly Dictionary<(string Accessor, string Receiver), AccessorDispatch> closedWorldAccessors = [];

        // The members an access to an accessor runs. A fixed accessor is its own body. A dispatched accessor is resolved on
        // the exact runtime types of the receiver: the static type when it is a struct, sealed or created where it is used,
        // the types the value is proved to have otherwise. A receiver nothing proves can be any authored type assignable to
        // its static type, so the implementations of all of those are followed: when the universe holds exactly one body it
        // is that body, and when it holds several the access is refused if any of them can reach a producer site.
        private AccessorDispatch ResolveAccessorDispatch(AuthoredMember member, IMethodSymbol accessor, ITypeSymbol? receiverType, ExpressionSyntax? receiver, bool exact, bool indexer)
        {
            Compilation compilation = member.Model.Compilation;

            if (!IsDispatchedAccessor(accessor))
            {
                AuthoredMember? fixedTarget = Resolve(accessor, compilation);

                if (fixedTarget is not null && HasEnumerationBody(fixedTarget))
                {
                    return new([fixedTarget], false);
                }

                // A property whose accessor cannot be found or has no body of its own is an auto-property, except an
                // indexer, which is never one.
                return new([], indexer && accessor.Locations.Any(static location => location.IsInSource));
            }

            ImplicitTargetCollector collector = new(this, member);

            List<INamedTypeSymbol> candidates = [];

            bool proved = false;

            if (receiverType is INamedTypeSymbol named && (exact || IsSealedOrValue(named)))
            {
                candidates.Add(named);

                proved = true;
            }
            else if (receiver is not null && receiverType is not null && receiver.SyntaxTree == member.Model.SyntaxTree)
            {
                EnumerationProof proof = new();

                if (ProveEnumerationLeaf(receiver, member.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member, proof))
                {
                    candidates.AddRange(proof.Candidates.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>());

                    proved = true;
                }
            }

            if (proved)
            {
                foreach (INamedTypeSymbol candidate in candidates)
                {
                    collector.Add(EnumerationImplementation(candidate, accessor), accessor);
                }

                return new(collector.Members, collector.Unresolved.Count != 0);
            }

            ITypeSymbol assignableTo = receiverType ?? accessor.ContainingType;

            (string, string) key = (accessor.ToDisplayString(), assignableTo.ToDisplayString());

            if (closedWorldAccessors.TryGetValue(key, out AccessorDispatch? known))
            {
                return known;
            }

            string assignableKey = assignableTo.OriginalDefinition.ToDisplayString();

            foreach (AuthoredInstantiable instantiable in AuthoredInstantiables())
            {
                if (assignableTo.TypeKind != TypeKind.TypeParameter && !instantiable.AssignableKeys.Contains(assignableKey))
                {
                    continue;
                }

                collector.Add(EnumerationImplementation(instantiable.Type, accessor), accessor);
            }

            // A slot the framework declares has its own implementation for a value that is not an authored type, so what an
            // authored override does is a question about the override, never the one body the access runs: it is refused
            // when an override can reach a site and otherwise runs nothing here. A slot the source declares is its own
            // implementation when the universe holds one body, and several bodies are refused when one can reach a site.
            bool declaredInSource = accessor.Locations.Any(static location => location.IsInSource);

            bool unresolved = collector.Unresolved.Count != 0 && declaredInSource;

            AccessorDispatch resolution = declaredInSource && collector.Members.Count == 1
                ? new(collector.Members, unresolved)
                : new([], unresolved || collector.Members.Count > (declaredInSource ? 1 : 0) && collector.Members.Any(SitesOnlyReaches));

            closedWorldAccessors[key] = resolution;

            return resolution;
        }

        // The receiver an access reads an accessor of: the type the compiler binds, the expression it was written as when
        // there is one, and whether the runtime type is the type itself (the object an initializer initializes was created
        // where the initializer is written).
        private static (ITypeSymbol? Type, ExpressionSyntax? Expression, bool Exact) AccessorReceiver(IOperation? instance)
        {
            if (instance is null)
            {
                return (null, null, false);
            }

            if (instance is IInstanceReferenceOperation reference)
            {
                return reference.ReferenceKind switch
                {
                    InstanceReferenceKind.ImplicitReceiver => (reference.Type, null, true),
                    InstanceReferenceKind.PatternInput => (reference.Type, PatternInputOf(reference), false),
                    _ => (reference.Type, null, false),
                };
            }

            return (instance.Type, instance.Syntax as ExpressionSyntax, false);
        }

        // The expression a property subpattern tests, when the pattern it belongs to is a top-level pattern.
        private static ExpressionSyntax? PatternInputOf(IInstanceReferenceOperation input)
        {
            for (IOperation? current = input.Parent; current is not null; current = current.Parent)
            {
                if (current is IRecursivePatternOperation recursive)
                {
                    return PatternInputExpression(recursive);
                }

                if (current is not (IPropertyReferenceOperation or IPropertySubpatternOperation))
                {
                    return null;
                }
            }

            return null;
        }

        // The authored builder an async method's state machine runs: the compiler calls its Create at the call, its Start to
        // run the machine, AwaitOnCompleted at each suspension and SetResult or SetException at the end, and reads its Task.
        private static INamedTypeSymbol? AuthoredAsyncMethodBuilder(IMethodSymbol method)
        {
            if (!method.IsAsync)
            {
                return null;
            }

            foreach (AttributeData attribute in method.GetAttributes().Concat(method.ReturnType.GetAttributes()))
            {
                if (attribute.AttributeClass is { Name: "AsyncMethodBuilderAttribute" }
                    && attribute.ConstructorArguments is [{ Value: INamedTypeSymbol builder }]
                    && builder.Locations.Any(static location => location.IsInSource))
                {
                    return builder;
                }
            }

            return null;
        }

        // Whether the members of the authored builder of an async method can reach a producer site or cannot be followed.
        private bool AsyncBuilderReachesProducerSite(AuthoredMember member, out INamedTypeSymbol? builder)
        {
            builder = AuthoredAsyncMethodBuilder(member.Symbol);

            if (builder is null)
            {
                return false;
            }

            foreach (IMethodSymbol builderMember in builder.GetMembers().OfType<IMethodSymbol>().Where(static candidate => !candidate.IsImplicitlyDeclared || candidate.MethodKind != MethodKind.Constructor))
            {
                if (Resolve(builderMember, member.Model.Compilation) is { } target)
                {
                    if (HasEnumerationBody(target) && ContainsPotentialProducerSite(target))
                    {
                        return true;
                    }
                }
                else if (!scanSitesOnly && (builderMember.IsAbstract || builderMember.IsExtern))
                {
                    return true;
                }
            }

            return false;
        }

        // The authored types whose static initialization (the static constructor and the initializers of static fields,
        // properties and events) can reach a classified producer site. The runtime runs it at the first use of the type, which
        // is no call and no position: a use of such a type, from another type, is refused. A graph that asks about sites
        // alone answers, once per compilation, so that no traversal ever has to enter an initializer.
        private static string HazardKey(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString() + "|" + type.ContainingAssembly?.Identity.Name;

        private IReadOnlySet<string>? staticInitializationHazardKeys;

        internal IReadOnlySet<string> StaticInitializationHazardKeys()
        {
            if (scanSitesOnly || analysedCompilations.Count == 0)
            {
                return new HashSet<string>();
            }

            if (staticInitializationHazardKeys is not null)
            {
                return staticInitializationHazardKeys;
            }

            ProducerGraph sites = SitesOnlyGraph();

            lock (sites)
            {
                return staticInitializationHazardKeys = sites.ComputeStaticInitializationHazardKeys();
            }
        }

        private IReadOnlySet<string> ComputeStaticInitializationHazardKeys()
        {
            HashSet<string> hazards = new(StringComparer.Ordinal);

            foreach (INamedTypeSymbol type in analysedCompilations.SelectMany(static compilation => SourceTypes(compilation.Assembly.GlobalNamespace)))
            {
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface) || type.IsImplicitlyDeclared || !type.Locations.Any(static location => location.IsInSource))
                {
                    continue;
                }

                if (StaticInitializationMembers(type).Any(initializer => ContainsPotentialProducerSite(initializer)))
                {
                    hazards.Add(HazardKey(type));
                }
            }

            return hazards;
        }

        internal IReadOnlyList<string> StaticInitializationHazardNames() => [.. StaticInitializationHazardKeys().Select(static key => key[..key.IndexOf('|', StringComparison.Ordinal)]).Order(StringComparer.Ordinal)];

        // The static constructor of a type and its static initializers, as members whose body is the initializer expression.
        private IEnumerable<AuthoredMember> StaticInitializationMembers(INamedTypeSymbol type)
        {
            IMethodSymbol? identity = type.StaticConstructors.FirstOrDefault() ?? type.InstanceConstructors.FirstOrDefault() ?? type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault();

            if (identity is null)
            {
                yield break;
            }

            foreach (IMethodSymbol staticConstructor in type.StaticConstructors)
            {
                if (Resolve(staticConstructor, analysedCompilations[0]) is { } authored && HasEnumerationBody(authored))
                {
                    yield return authored;
                }
            }

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
                        FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword) =>
                            field.Declaration.Variables.Select(static variable => variable.Initializer?.Value).OfType<ExpressionSyntax>(),
                        PropertyDeclarationSyntax { Initializer.Value: { } value } property when property.Modifiers.Any(SyntaxKind.StaticKeyword) => [value],
                        EventFieldDeclarationSyntax @event when @event.Modifiers.Any(SyntaxKind.StaticKeyword) =>
                            @event.Declaration.Variables.Select(static variable => variable.Initializer?.Value).OfType<ExpressionSyntax>(),
                        _ => [],
                    };

                    foreach (ExpressionSyntax value in values)
                    {
                        yield return new AuthoredMember(identity, value, model);
                    }
                }
            }
        }

        // The authored type whose static initialization the use at the node starts, when that initialization can reach a
        // producer site: a static member of the type, the creation of a type, or of a type derived from it, from code of
        // another type. Code of the type itself runs only once its initialization has started.
        private INamedTypeSymbol? StaticInitializationStartedAt(AuthoredMember member, SyntaxNode node)
        {
            if (node is not (MemberAccessExpressionSyntax or IdentifierNameSyntax or BaseObjectCreationExpressionSyntax))
            {
                return null;
            }

            IReadOnlySet<string> hazards = StaticInitializationHazardKeys();

            if (hazards.Count == 0)
            {
                return null;
            }

            ISymbol? symbol = member.Model.GetSymbolInfo(node).Symbol;

            if (symbol is null || !(symbol.IsStatic || symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }) || symbol.ContainingType is not { } owner)
            {
                return null;
            }

            for (INamedTypeSymbol? current = owner; current is not null; current = symbol is IMethodSymbol ? current.BaseType : null)
            {
                if (member.Symbol.ContainingType is { } enclosing && HazardKey(enclosing) == HazardKey(current))
                {
                    return null;
                }

                if (hazards.Contains(HazardKey(current)))
                {
                    return current;
                }
            }

            return null;
        }

        // The refusals the syntax lanes make at a node, for the transitive scan: the scan answers whether the traversal of a
        // member can report a site or refuse, so it has to say yes wherever the traversal would refuse. A dynamic call or
        // indexer has no target; a framework member that enumerates an authored sequence is refused unless that sequence
        // is free of producer sites; a user-defined operator the operator lane cannot follow (a conditional logical
        // operator, a lifted operator, a generated record operator it cannot certify) is refused whatever its body does.
        private bool LaneRefusalsReachProducerSite(AuthoredMember member, SyntaxNode node, HashSet<GraphMemberIdentity> path)
        {
            // What follows are the refusals the traversal makes, which a question about sites alone does not ask.
            if (scanSitesOnly)
            {
                return false;
            }

            if (StaticInitializationStartedAt(member, node) is not null)
            {
                return true;
            }

            if (node is InvocationExpressionSyntax or ElementAccessExpressionSyntax or ElementBindingExpressionSyntax or ImplicitElementAccessSyntax
                && member.Model.GetOperation(node) is IDynamicInvocationOperation or IDynamicIndexerAccessOperation)
            {
                return true;
            }

            if (node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
                && member.Model.GetSymbolInfo(node).Symbol is IMethodSymbol called
                && Resolve(called, member.Model.Compilation) is null
                && called.Locations.All(static location => !location.IsInSource)
                && (FrameworkMemberRunsAuthoredEnumerator(member, node, called, out _) || FrameworkMemberRunsAuthoredCallback(member, node, called, out _)))
            {
                return true;
            }

            if (UserDefinedOperatorAt(member, node) is not { } selection)
            {
                return false;
            }

            if (Resolve(selection.Method, member.Model.Compilation) is not { } operatorTarget)
            {
                bool lifted = selection.Unsupported
                    && member.Model.GetOperation(node) is IBinaryOperation { IsLifted: true, OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals };

                List<AuthoredMember> equalityTargets = [];

                bool certified = (!selection.Unsupported || lifted)
                    && TryGeneratedRecordEqualityTargets(member, selection.Method, selection.Operands, out equalityTargets, out _, lifted);

                if (equalityTargets.Any(target => ContainsPotentialProducerSite(target, path)))
                {
                    return true;
                }

                return !certified && !IsReviewedEffectFreeExternalMember(selection.Method, node, member.Model, member);
            }

            return selection.Unsupported
                || operatorTarget.Syntax is not OperatorDeclarationSyntax { Body: not null } and not OperatorDeclarationSyntax { ExpressionBody: not null }
                || !selection.Method.IsStatic
                || selection.Method.Parameters.Length != selection.Operands.Length
                || selection.Method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None)
                || selection.Operands.Any(static operand => operand.Syntax is not ExpressionSyntax);
        }

        // Whether the members one implicit step resolves to can reach a producer site, for the transitive scan: an authored
        // body that does, a framework member that is a site, and (unless only sites are asked) a member that cannot be
        // followed or is not reviewed.
        private bool ImplicitTargetsReachProducerSite(AuthoredMember member, ImplicitTargets targets, SyntaxNode anchor, HashSet<GraphMemberIdentity> path) =>
            !scanSitesOnly && targets.Unresolved.Count != 0
            || targets.Members.Any(target => ContainsPotentialProducerSite(target, path))
            || targets.Framework.Any(framework => Classify(framework, Normalize(framework), anchor, member) is not null || !scanSitesOnly && !IsReviewedImplicitFrameworkMember(member, framework, anchor));

        // Whether an implicit call can reach a producer site, for the transitive scan an opaque callback body gets.
        private bool ImplicitCallReachesProducerSite(AuthoredMember member, ImplicitCall call, HashSet<GraphMemberIdentity> path)
        {
            if (call.RefusalCode is not null)
            {
                return !scanSitesOnly;
            }

            if (call.Kind == ImplicitCallKind.QueryClause)
            {
                return call.Callbacks?.Any(callback => ContainsPotentialProducerSite(member with { Syntax = callback }, path)) == true;
            }

            foreach (ImplicitCallStep step in call.Steps)
            {
                ImplicitTargets targets = ResolveImplicitStep(member, step);

                if (targets.Framework.Count != 0 && step.Values is { Count: > 0 } && FrameworkValuesRunAuthoredCallbacks(member, step.Values)
                    || ImplicitTargetsReachProducerSite(member, targets, call.Anchor, path))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
