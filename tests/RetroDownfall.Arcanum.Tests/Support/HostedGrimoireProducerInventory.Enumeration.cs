using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

using System.Collections.Concurrent;

using System.Collections.Immutable;

using System.Reflection;

using System.Text.Json;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

// Implicit enumeration. A foreach statement, an await foreach statement and a collection-expression spread
// execute GetEnumerator (or GetAsyncEnumerator), MoveNext (or MoveNextAsync), Current and Dispose (or
// DisposeAsync) of their operand without any call being written. Each of those members, and a user-defined
// element conversion, is a call made where the loop or the spread executes, so the traversal models it there,
// under the frontiers live at that point.
internal static partial class HostedGrimoireProducerInventory
{
    // The reviewed closed set. Enumerating a value whose static type is one of these runs only framework code over
    // in-memory storage: the members are non-virtual, so the static type fixes them whatever the runtime type is,
    // and no enumerator member calls back into element, comparer or caller code. The nested Enumerator types are
    // the enumerators those collections hand out. Nothing outside this table, and no type from another assembly
    // that merely shares a name, is closed.
    private static readonly IReadOnlyDictionary<string, AssemblyName> ClosedEnumerationAnchors = BuildClosedEnumerationAnchors();

    private static IReadOnlyDictionary<string, AssemblyName> BuildClosedEnumerationAnchors()
    {
        Dictionary<string, AssemblyName> anchors = new(StringComparer.Ordinal);

        void Collection(string key, Type anchor, bool enumerator = true)
        {
            anchors[key] = anchor.Assembly.GetName();

            if (enumerator)
            {
                anchors[key + ".Enumerator"] = anchor.Assembly.GetName();
            }
        }

        Collection("System.Collections.Generic.List`1", typeof(List<>));

        Collection("System.Collections.Generic.Dictionary`2", typeof(Dictionary<,>));

        Collection("System.Collections.Generic.Dictionary`2.KeyCollection", typeof(Dictionary<,>));

        Collection("System.Collections.Generic.Dictionary`2.ValueCollection", typeof(Dictionary<,>));

        Collection("System.Collections.Generic.HashSet`1", typeof(HashSet<>));

        Collection("System.Collections.Generic.Queue`1", typeof(Queue<>));

        Collection("System.Collections.Generic.Stack`1", typeof(Stack<>));

        Collection("System.Collections.Concurrent.ConcurrentDictionary`2", typeof(ConcurrentDictionary<,>), enumerator: false);

        Collection("System.Collections.Concurrent.ConcurrentQueue`1", typeof(ConcurrentQueue<>), enumerator: false);

        Collection("System.Collections.Immutable.ImmutableArray`1", typeof(ImmutableArray<>));

        Collection("System.Span`1", typeof(Span<>));

        Collection("System.ReadOnlySpan`1", typeof(ReadOnlySpan<>));

        Collection("System.Text.Json.JsonElement.ArrayEnumerator", typeof(JsonElement), enumerator: false);

        Collection("System.Text.Json.JsonElement.ObjectEnumerator", typeof(JsonElement), enumerator: false);

        Collection("System.Text.StringRuneEnumerator", typeof(string), enumerator: false);

        Collection("System.Text.SpanRuneEnumerator", typeof(string), enumerator: false);

        Collection("System.CharEnumerator", typeof(string), enumerator: false);

        // A segment of an array and its enumerator walk that array; a StringBuilder's chunk enumerator walks its own chunks.
        Collection("System.ArraySegment`1", typeof(ArraySegment<>));

        Collection("System.Text.StringBuilder.ChunkEnumerator", typeof(System.Text.StringBuilder), enumerator: false);

        // The regular-expression engine's match list: a non-virtual GetEnumerator over matches the engine produced.
        Collection("System.Text.RegularExpressions.MatchCollection", typeof(System.Text.RegularExpressions.Regex), enumerator: false);

        return anchors;
    }

    // Framework types whose enumeration members are safe to run only when the runtime type is exactly known, so a
    // static type naming them is not closed but an allocation or a sealed static type is. SortedSet<T> can be a view
    // that calls its comparer; the immutable array builder is a sealed in-memory list; ReadOnlyCollectionBase and
    // GenericAcl declare the members that sealed access-control collections inherit.
    private static readonly IReadOnlyDictionary<string, AssemblyName> ClosedEnumerationImplementerAnchors = BuildClosedEnumerationImplementerAnchors();

    private static IReadOnlyDictionary<string, AssemblyName> BuildClosedEnumerationImplementerAnchors()
    {
        Dictionary<string, AssemblyName> anchors = new(ClosedEnumerationAnchors, StringComparer.Ordinal);

        anchors["System.Collections.Generic.SortedSet`1"] = typeof(SortedSet<>).Assembly.GetName();

        anchors["System.Collections.Generic.SortedSet`1.Enumerator"] = typeof(SortedSet<>).Assembly.GetName();

        anchors["System.Collections.Immutable.ImmutableArray`1.Builder"] = typeof(System.Collections.Immutable.ImmutableArray<>).Assembly.GetName();

        anchors["System.Collections.ReadOnlyCollectionBase"] = typeof(System.Collections.ReadOnlyCollectionBase).Assembly.GetName();

        anchors["System.Security.AccessControl.GenericAcl"] = typeof(System.Security.AccessControl.GenericAcl).Assembly.GetName();

        anchors["System.Security.AccessControl.AceEnumerator"] = typeof(System.Security.AccessControl.AceEnumerator).Assembly.GetName();

        return anchors;
    }

    // The framework runtime types the implementer-only members above are trusted for. A member declared by an
    // unsealed framework type (GenericAcl, ReadOnlyCollectionBase, SortedSet<T>) calls abstract or virtual
    // members of its subclass, so it is safe to run only when the value is exactly one of these types: an
    // authored subclass that overrides those members is not, whatever the declaring type is.
    private static readonly IReadOnlyDictionary<string, AssemblyName> ExactEnumerationRuntimeAnchors = BuildExactEnumerationRuntimeAnchors();

    private static IReadOnlyDictionary<string, AssemblyName> BuildExactEnumerationRuntimeAnchors() =>
        new Dictionary<string, AssemblyName>(StringComparer.Ordinal)
        {
            ["System.Collections.Generic.SortedSet`1"] = typeof(SortedSet<>).Assembly.GetName(),
            ["System.Collections.Generic.SortedSet`1.Enumerator"] = typeof(SortedSet<>).Assembly.GetName(),
            ["System.Collections.Immutable.ImmutableArray`1.Builder"] = typeof(System.Collections.Immutable.ImmutableArray<>).Assembly.GetName(),
            ["System.Security.AccessControl.RawAcl"] = typeof(System.Security.AccessControl.RawAcl).Assembly.GetName(),
            ["System.Security.AccessControl.AuthorizationRuleCollection"] = typeof(System.Security.AccessControl.AuthorizationRuleCollection).Assembly.GetName(),
            ["System.Security.AccessControl.AceEnumerator"] = typeof(System.Security.AccessControl.AceEnumerator).Assembly.GetName(),
        };

    // The enumeration contracts whose implementations the Authored Enumerables Absent premise excludes from
    // the analysed source, and the contracts under which a value may be assumed non-authored once it holds.
    private static readonly IReadOnlySet<string> EnumerationContracts = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Collections.IEnumerable",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IAsyncEnumerable`1",
        "System.Collections.IEnumerator",
        "System.Collections.Generic.IEnumerator`1",
        "System.Collections.Generic.IAsyncEnumerator`1",
    };

    // The framework collection interfaces a premise-covered operand may be statically typed as.
    private static readonly IReadOnlySet<string> PremiseCoveredEnumerationInterfaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Collections.IEnumerable",
        "System.Collections.ICollection",
        "System.Collections.IList",
        "System.Collections.IDictionary",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IAsyncEnumerable`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Linq.IOrderedEnumerable`1",
        "System.Linq.IGrouping`2",
    };

    private static bool IsEnumerationContract(ITypeSymbol type) =>
        type is INamedTypeSymbol named && EnumerationContracts.Contains(TypeKey(named));

    private static bool IsEnumerableValueType(ITypeSymbol? type) =>
        type is not null
        && type.SpecialType != SpecialType.System_String
        && (IsEnumerationContract(type) || type.AllInterfaces.Any(IsEnumerationContract));

    // Authored Enumerables Absent. No type declared in an analysed source assembly implements an enumeration
    // contract, directly or through a base type or an interface, or declares a GetEnumerator or
    // GetAsyncEnumerator member (an instance method, an explicit implementation, an interface default or an
    // extension). A value statically typed as a framework collection interface then has no authored runtime
    // type to dispatch to. Compiler-generated iterators are the one authored enumerator the premise leaves
    // alone: their bodies run where they are consumed, and the call that produced them is traversed with its
    // lazy-consumption ownership proof. An unresolved contract or base type means the premise is not granted.
    private static string[] VerifyAuthoredEnumerablesAbsent(IReadOnlyList<CSharpCompilation> compilations)
    {
        HashSet<string> findings = new(StringComparer.Ordinal);

        foreach (CSharpCompilation compilation in compilations)
        {
            foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                // A C# 14 extension block is exposed as a nested type of its own kind whose members are the block's
                // members, and as implicitly declared implementation methods of the static class that holds it. Both
                // are read, so an extension GetEnumerator is found whichever way the symbols present it.
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Extension))
                {
                    continue;
                }

                string name = SourceTypeName(type);

                for (INamedTypeSymbol? parent = type.BaseType; parent is not null; parent = parent.BaseType)
                {
                    if (parent.TypeKind == TypeKind.Error)
                    {
                        findings.Add($"'{name}' of '{compilation.AssemblyName}' has an unresolved base type, so an authored enumerable cannot be excluded (unresolved reference).");

                        break;
                    }
                }

                foreach (INamedTypeSymbol contract in type.AllInterfaces)
                {
                    if (contract.TypeKind == TypeKind.Error)
                    {
                        findings.Add($"'{name}' of '{compilation.AssemblyName}' has an unresolved interface, so an authored enumerable cannot be excluded (unresolved reference).");
                    }
                    else if (EnumerationContracts.Contains(TypeKey(contract)))
                    {
                        findings.Add($"'{name}' of '{compilation.AssemblyName}' implements {TypeKey(contract)}, so a value typed as a framework collection interface may dispatch to authored enumeration members.");
                    }
                }

                foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.Name.EndsWith("GetEnumerator", StringComparison.Ordinal)
                        || method.Name.EndsWith("GetAsyncEnumerator", StringComparison.Ordinal))
                    {
                        findings.Add($"'{name}' of '{compilation.AssemblyName}' declares {method.Name}, so a foreach over a value of an authored type runs authored enumeration members.");
                    }
                }
            }
        }

        return [.. findings.Order(StringComparer.Ordinal)];
    }

    private sealed partial class ProducerGraph
    {
        private static bool IsClosedFrameworkEnumerationType(ITypeSymbol? type)
        {
            if (type is IArrayTypeSymbol || type?.SpecialType == SpecialType.System_String)
            {
                return true;
            }

            return type is INamedTypeSymbol named
                && ClosedEnumerationAnchors.TryGetValue(TypeKey(named), out AssemblyName? anchor)
                && IsExactFrameworkType(named, TypeKey(named), anchor);
        }

        // True for a framework type whose declared enumeration members are safe to run: a closed type, or one of the
        // implementer-only types that is trusted only for the exact runtime type that reaches it.
        private static bool IsClosedEnumerationImplementer(ITypeSymbol? type) =>
            IsClosedFrameworkEnumerationType(type)
            || type is INamedTypeSymbol named
                && ClosedEnumerationImplementerAnchors.TryGetValue(TypeKey(named), out AssemblyName? anchor)
                && IsExactFrameworkType(named, TypeKey(named), anchor);

        private static bool IsExactEnumerationRuntimeType(ITypeSymbol? type) =>
            type is INamedTypeSymbol named
            && ExactEnumerationRuntimeAnchors.TryGetValue(TypeKey(named), out AssemblyName? anchor)
            && IsExactFrameworkType(named, TypeKey(named), anchor);

        // A framework implementation resolved for a runtime type is trusted when the type that declares it is closed
        // (its members are non-virtual, so no subclass replaces them), or when the runtime type itself is one of the
        // exact framework types and the declaring type is a reviewed implementer. The declaring type alone never
        // decides: an authored subclass of GenericAcl, ReadOnlyCollectionBase or SortedSet<T> inherits their members
        // and supplies the abstract and virtual ones they call.
        private static bool IsTrustedFrameworkEnumerationImplementation(ITypeSymbol runtimeType, ISymbol implementation) =>
            IsClosedFrameworkEnumerationType(implementation.ContainingType)
            || IsExactEnumerationRuntimeType(runtimeType) && IsClosedEnumerationImplementer(implementation.ContainingType);

        // True for a framework collection interface whose implementation set is, under the Authored Enumerables Absent
        // premise, free of authored types. A trusted framework identity is required: an authored interface of the
        // same name never qualifies.
        private static bool IsPremiseCoveredEnumerationInterface(ITypeSymbol? type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Interface } named
            && PremiseCoveredEnumerationInterfaces.Contains(TypeKey(named))
            && !named.Locations.Any(static location => location.IsInSource)
            && (FrameworkAssemblyIdentityMatches(named.ContainingAssembly.Identity, typeof(IEnumerable<>).Assembly.GetName())
                || FrameworkAssemblyIdentityMatches(named.ContainingAssembly.Identity, typeof(System.Collections.IList).Assembly.GetName())
                || FrameworkAssemblyIdentityMatches(named.ContainingAssembly.Identity, typeof(Enumerable).Assembly.GetName()));

        private enum EnumerationClosureKind : byte
        {
            // The operand's enumeration members are framework members of a closed framework type, or of a
            // framework value built from closed values. Authored work it reaches (an iterator body, a lazy
            // callback) is traversed where that value was produced and its consumption proven.
            Closed = 1,

            // Every runtime type the operand can have is one of the exact candidate types.
            Candidates = 2,

            Unproven = 3,
        }

        private sealed record EnumerationClosure(EnumerationClosureKind Kind, IReadOnlyList<INamedTypeSymbol> Candidates)
        {
            internal static EnumerationClosure Of(EnumerationClosureKind kind) => new(kind, []);
        }

        private sealed class EnumerationProof
        {
            internal const int MaximumWork = 256;

            internal int RemainingWork { get; set; } = MaximumWork;

            internal List<INamedTypeSymbol> Candidates { get; } = [];

            internal HashSet<GraphMemberIdentity> AuthoredCalls { get; } = [];
        }

        // The members the compiler selected for one enumeration site. For a foreach the semantic model reports
        // them; for a spread the same foreach binding is replayed on the spread's operand.
        private sealed record EnumerationBinding(
            ExpressionSyntax Operand,
            ITypeSymbol? OperandType,
            bool Asynchronous,
            IMethodSymbol? GetEnumerator,
            IMethodSymbol? MoveNext,
            IPropertySymbol? Current,
            IMethodSymbol? Dispose,
            IReadOnlyList<IMethodSymbol> Conversions,
            bool Bound,
            DeconstructionInfo? Deconstruction = null,
            ITypeSymbol? ElementType = null);

        // LazyIterators are the calls, among the return expressions of an enumerator provider, to authored iterator
        // methods that return the enumerator: their bodies run on the MoveNext calls that follow, not at the call.
        private sealed record EnumerationTarget(string Step, AuthoredMember Member, EnumerationStepTiming Timing, IReadOnlyList<SyntaxNode>? LazyIterators = null);

        private enum EnumerationStepTiming : byte
        {
            // Once, after the operand is evaluated.
            Entry = 1,

            // Once per iteration, at the loop head, which a back-edge reaches after the whole body.
            Iteration = 2,

            // Once, when the loop or the enumerator's owner exits.
            Exit = 3,

            // A compiler-generated iterator runs its whole body lazily, from the first MoveNext to Dispose.
            Lazy = 4,
        }

        private sealed record EnumerationResolution(
            IReadOnlyList<EnumerationTarget> Targets,
            IReadOnlyList<string> Unresolved);

        // The calls to authored iterator methods that an enumerator provider returns, while the provider is traversed at a
        // loop whose frontier is proved to hold across every MoveNext. Scoped to that traversal.
        private readonly HashSet<SyntaxNode> ownedLazyEnumeratorCalls = new(ReferenceEqualityComparer.Instance);

        // A binding depends only on the syntax and the semantic model that binds it, never on a traversal state.
        private readonly Dictionary<SyntaxNode, (SemanticModel Model, EnumerationBinding Binding)> enumerationBindings = new(ReferenceEqualityComparer.Instance);

        private EnumerationBinding BindEnumeration(AuthoredMember member, SyntaxNode node)
        {
            if (enumerationBindings.TryGetValue(node, out (SemanticModel Model, EnumerationBinding Binding) cached)
                && ReferenceEquals(cached.Model, member.Model))
            {
                return cached.Binding;
            }

            EnumerationBinding binding = BindEnumerationUncached(member, node);

            enumerationBindings[node] = (member.Model, binding);

            return binding;
        }

        private EnumerationBinding BindEnumerationUncached(AuthoredMember member, SyntaxNode node)
        {
            if (node is CommonForEachStatementSyntax loop)
            {
                ForEachStatementInfo info = member.Model.GetForEachStatementInfo(loop);

                ITypeSymbol? operandType = member.Model.GetTypeInfo(loop.Expression).Type;

                bool asynchronous = loop.AwaitKeyword.RawKind != 0;

                return new(
                    loop.Expression,
                    operandType,
                    asynchronous,
                    info.GetEnumeratorMethod,
                    info.MoveNextMethod,
                    info.CurrentProperty,
                    info.DisposeMethod,
                    UserConversionMethods(info.ElementConversion, info.CurrentConversion),
                    info.GetEnumeratorMethod is not null && info.MoveNextMethod is not null && info.CurrentProperty is not null,
                    loop is ForEachVariableStatementSyntax variableLoop ? member.Model.GetDeconstructionInfo(variableLoop) : null,
                    info.ElementType);
            }

            if (node is SpreadElementSyntax spread)
            {
                EnumerationBinding replay = BindEnumerationExpression(member, spread.Expression, spread.SpanStart, asynchronous: false);

                if (member.Model.GetOperation(spread) is ISpreadOperation { ElementConversion: { } elementConversion }
                    && elementConversion.MethodSymbol is { } converter)
                {
                    return replay with { Conversions = [.. replay.Conversions, converter] };
                }

                return replay;
            }

            throw new InvalidOperationException("An enumeration site is a foreach statement or a spread element.");
        }

        private static IReadOnlyList<IMethodSymbol> UserConversionMethods(Conversion element, Conversion current)
        {
            List<IMethodSymbol> methods = [];

            if (element.MethodSymbol is { } elementMethod)
            {
                methods.Add(elementMethod);
            }

            if (current.MethodSymbol is { } currentMethod)
            {
                methods.Add(currentMethod);
            }

            return methods;
        }

        // Replays the compiler's foreach binding on an expression that is not a loop operand, such as a spread
        // element or a sequence handed to a framework member, by binding a synthetic foreach statement at the
        // expression's position in the same semantic model.
        private EnumerationBinding BindEnumerationExpression(AuthoredMember member, ExpressionSyntax operand, int position, bool asynchronous)
        {
            ITypeSymbol? operandType = member.Model.GetTypeInfo(operand).Type;

            CSharpParseOptions? options = operand.SyntaxTree.Options as CSharpParseOptions;

            StatementSyntax statement = SyntaxFactory.ParseStatement(
                (asynchronous ? "await " : string.Empty) + "foreach (var __enumerated in " + operand.ToFullString() + ") ;",
                options: options);

            if (statement is not CommonForEachStatementSyntax synthetic)
            {
                return new(operand, operandType, asynchronous, null, null, null, null, [], false);
            }

            if (!member.Model.TryGetSpeculativeSemanticModel(position, statement, out SemanticModel? speculative) || speculative is null)
            {
                return new(operand, operandType, asynchronous, null, null, null, null, [], false);
            }

            ForEachStatementInfo info = speculative.GetForEachStatementInfo(synthetic);

            return new(
                operand,
                operandType,
                asynchronous,
                info.GetEnumeratorMethod,
                info.MoveNextMethod,
                info.CurrentProperty,
                info.DisposeMethod,
                UserConversionMethods(info.ElementConversion, info.CurrentConversion),
                info.GetEnumeratorMethod is not null && info.MoveNextMethod is not null && info.CurrentProperty is not null);
        }

        private EnumerationClosure ClassifyEnumeration(AuthoredMember member, EnumerationBinding binding)
        {
            // The compiler-selected members are needed only to follow exact candidates; a closed operand needs none,
            // so a spread whose position cannot host a synthetic statement (a field or property initializer) is
            // still classified by what its operand is.
            if (binding.OperandType is not { } type || type.TypeKind is TypeKind.Error or TypeKind.Dynamic)
            {
                return EnumerationClosure.Of(EnumerationClosureKind.Unproven);
            }

            if (IsClosedFrameworkEnumerationType(type))
            {
                return EnumerationClosure.Of(EnumerationClosureKind.Closed);
            }

            EnumerationProof proof = new();

            if (ProveEnumerationLeaf(binding.Operand, member.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member, proof))
            {
                INamedTypeSymbol[] candidates = [.. proof.Candidates.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>()];

                return candidates.Length == 0
                    ? EnumerationClosure.Of(EnumerationClosureKind.Closed)
                    : new(EnumerationClosureKind.Candidates, candidates);
            }

            return EnumerationClosure.Of(EnumerationClosureKind.Unproven);
        }

        private static bool IsSequenceOrEnumeratorType(ITypeSymbol type) =>
            TypeKey(type) is "System.Collections.IEnumerable"
                or "System.Collections.Generic.IEnumerable`1"
                or "System.Collections.Generic.IAsyncEnumerable`1"
                or "System.Collections.IEnumerator"
                or "System.Collections.Generic.IEnumerator`1"
                or "System.Collections.Generic.IAsyncEnumerator`1";

        private static bool IsFrameworkCollectionExpression(SemanticModel model, ExpressionSyntax expression)
        {
            if (expression is not CollectionExpressionSyntax || model.GetTypeInfo(expression).ConvertedType is not { } converted)
            {
                return false;
            }

            return converted is IArrayTypeSymbol
                || converted is INamedTypeSymbol named
                    && TypeKey(named) is "System.Collections.Generic.List`1"
                        or "System.Collections.Generic.IReadOnlyList`1"
                        or "System.Collections.Generic.IList`1"
                        or "System.Collections.Generic.IEnumerable`1"
                        or "System.Collections.Generic.IReadOnlyCollection`1"
                        or "System.Collections.Generic.ICollection`1"
                    && IsExactFrameworkType(named, TypeKey(named), typeof(List<>).Assembly.GetName());
        }

        // Proves that every runtime value of the expression is either a closed framework value or one of the exact
        // candidate types it records. An expression whose runtime type cannot be followed is unproven; nothing here
        // assumes a framework type for an interface-typed storage location.
        private bool ProveEnumerationLeaf(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context,
            EnumerationProof proof)
        {
            int recorded = proof.Candidates.Count;

            if (ProveEnumerationLeafCore(expression, model, path, context, proof))
            {
                return true;
            }

            // Authored Enumerables Absent: the value could not be followed, but no authored type provides
            // enumeration members, so a value of a framework collection interface is not an authored one.
            if (AuthoredEnumerablesAbsentByUse
                && model.GetTypeInfo(StripTransparentExpression(expression)).Type is { } type
                && IsPremiseCoveredEnumerationInterface(type))
            {
                proof.Candidates.RemoveRange(recorded, proof.Candidates.Count - recorded);

                return true;
            }

            return false;
        }

        private bool ProveEnumerationLeafCore(
            ExpressionSyntax expression,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context,
            EnumerationProof proof)
        {
            if (proof.RemainingWork-- <= 0)
            {
                return false;
            }

            ExpressionSyntax written = expression;

            expression = StripTransparentExpression(expression);

            // A user-defined conversion changes the runtime value: what the operator returns is not what it converts,
            // so the converted expression says nothing about it. That holds for a written cast and equally for an
            // implicit conversion the context applies (an initializer, a return, an argument binding).
            if (HasUserDefinedConversion(model, written)
                || model.GetConversion(written).IsUserDefined
                || model.GetConversion(expression).IsUserDefined)
            {
                return false;
            }

            switch (expression)
            {
                case ConditionalExpressionSyntax conditional:
                    return ProveEnumerationLeaf(conditional.WhenTrue, model, path, context, proof)
                        && ProveEnumerationLeaf(conditional.WhenFalse, model, path, context, proof);

                case BinaryExpressionSyntax coalescing when coalescing.IsKind(SyntaxKind.CoalesceExpression):
                    return ProveEnumerationLeaf(coalescing.Left, model, path, context, proof)
                        && ProveEnumerationLeaf(coalescing.Right, model, path, context, proof);

                case SwitchExpressionSyntax selection:
                    return selection.Arms.All(arm => ProveEnumerationLeaf(arm.Expression, model, path, context, proof));

                case ThrowExpressionSyntax:
                    return true;

                case DefaultExpressionSyntax:
                    return ProveDefaultValue(expression, written, model, proof);

                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression) || literal.IsKind(SyntaxKind.DefaultLiteralExpression):
                    return ProveDefaultValue(expression, written, model, proof);

                case CollectionExpressionSyntax:
                    return IsFrameworkCollectionExpression(model, expression);

                case AwaitExpressionSyntax awaited:
                    if (model.GetTypeInfo(expression).Type is { } resultType && IsClosedFrameworkEnumerationType(resultType))
                    {
                        return true;
                    }

                    ExpressionSyntax inner = StripTransparentExpression(awaited.Expression);

                    if (inner is InvocationExpressionSyntax configured
                        && model.GetSymbolInfo(configured).Symbol is IMethodSymbol configuredMethod
                        && IsExactFrameworkConfigureAwait(configuredMethod)
                        && configured.Expression is MemberAccessExpressionSyntax { Expression: { } unconfigured })
                    {
                        inner = StripTransparentExpression(unconfigured);
                    }

                    return ProveEnumerationLeaf(inner, model, path, context, proof);
            }

            ITypeSymbol? type = model.GetTypeInfo(expression).Type;

            if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Dynamic)
            {
                return false;
            }

            if (IsClosedFrameworkEnumerationType(type) || IsFrameworkOwnedSequenceProperty(expression, model))
            {
                return true;
            }

            // The reviewed primitive priority queue's unordered items: a default queue whose elements and
            // priorities are primitive, so enumerating it runs no comparer and no conversion. The review judges the
            // origin in the caller's context, which is meaningful only for an expression of the caller's own tree.
            if (context is not null
                && context.Model.SyntaxTree == expression.SyntaxTree
                && HasReviewedPrimitiveQueueItems(expression, model, context))
            {
                return true;
            }

            if (expression is BaseObjectCreationExpressionSyntax && type is INamedTypeSymbol created)
            {
                proof.Candidates.Add(created);

                return true;
            }

            if (expression is InvocationExpressionSyntax call
                && ProveEnumerationInvocation(call, model, path, context, proof) is { } invocationProof)
            {
                return invocationProof;
            }

            bool Flow() => HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                (source, sourceModel, sourcePath, sourceContext) => ProveEnumerationLeaf(source, sourceModel, sourcePath, sourceContext, proof));

            if (type is INamedTypeSymbol exact
                && (exact.TypeKind == TypeKind.Struct || exact is { TypeKind: TypeKind.Class, IsSealed: true }))
            {
                // An authored type, or a framework type whose members are trusted, is its own exact candidate. Any other
                // framework value type (a stored wrapper such as the ConfigureAwait struct) is first followed to where it
                // was built, and is an exact candidate only when that cannot be followed.
                int recorded = proof.Candidates.Count;

                if (exact.Locations.Any(static location => location.IsInSource)
                    || IsClosedEnumerationImplementer(exact)
                    || !Flow())
                {
                    proof.Candidates.RemoveRange(recorded, proof.Candidates.Count - recorded);

                    proof.Candidates.Add(exact);
                }

                return true;
            }

            return Flow();
        }

        // AggregateException.InnerExceptions is a read-only view over the array the exception copied from its
        // constructor arguments. The list is framework-owned and its elements are exceptions that are never enumerated
        // as sequences, so enumerating it runs no authored code. It is the one reviewed read-only view: any other view
        // wraps whatever list it was given. The property is not virtual, so an authored subclass cannot replace it.
        private static bool IsFrameworkOwnedSequenceProperty(ExpressionSyntax expression, SemanticModel model) =>
            expression is MemberAccessExpressionSyntax
            && model.GetSymbolInfo(expression).Symbol is IPropertySymbol { Name: "InnerExceptions", IsVirtual: false, IsOverride: false } property
            && IsExactFrameworkType(property.ContainingType, "System.AggregateException", typeof(AggregateException).Assembly.GetName());

        // The default of a value type is a real value whose own members run, so it is an exact candidate. The
        // default of a reference type is null: it has no instance members to run, and the static or extension
        // GetEnumerator that a null receiver can still reach is the compiler-selected member, which the binding
        // follows whatever the operand is. A type parameter is neither, and stays unproven.
        private static bool ProveDefaultValue(ExpressionSyntax expression, ExpressionSyntax written, SemanticModel model, EnumerationProof proof)
        {
            // A null or default literal inside a cast carries no type of its own; the cast that wraps it does.
            Microsoft.CodeAnalysis.TypeInfo info = model.GetTypeInfo(expression);

            Microsoft.CodeAnalysis.TypeInfo outer = model.GetTypeInfo(written);

            ITypeSymbol? type = info.Type ?? info.ConvertedType ?? outer.Type ?? outer.ConvertedType;

            if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.TypeParameter)
            {
                return false;
            }

            if (type.IsReferenceType)
            {
                return true;
            }

            if (type is INamedTypeSymbol { IsValueType: true } valueType)
            {
                proof.Candidates.Add(valueType);

                return true;
            }

            return false;
        }

        private static bool HasUserDefinedConversion(SemanticModel model, ExpressionSyntax expression)
        {
            IOperation? operation = model.GetOperation(expression);

            while (operation is not null)
            {
                switch (operation)
                {
                    case IConversionOperation conversion:
                        if (conversion.OperatorMethod is not null || conversion.Conversion.IsUserDefined)
                        {
                            return true;
                        }

                        operation = conversion.Operand;

                        break;

                    case IParenthesizedOperation parenthesized:
                        operation = parenthesized.Operand;

                        break;

                    default:
                        return false;
                }
            }

            return false;
        }

        // The exact framework Enumerable.ToList and Enumerable.ToArray: each builds a fresh list or array, so the value
        // is closed whatever the source was. Enumerating that source is the call's own concern.
        private static bool IsFrameworkMaterialization(IMethodSymbol method) =>
            (method.ReducedFrom ?? method) is
            {
                IsStatic: true,
                Arity: 1,
                Parameters: [IParameterSymbol parameter],
                Name: "ToList" or "ToArray",
            } materializer
            && TypeKey(parameter.Type) == "System.Collections.Generic.IEnumerable`1"
            && IsExactFrameworkType(materializer.ContainingType, "System.Linq.Enumerable", typeof(Enumerable).Assembly.GetName());

        // Null means the call produces a value this proof does not follow through the call (a framework or external
        // member returning a concrete type outside the closed set), so the caller judges the value by its type.
        private bool? ProveEnumerationInvocation(
            InvocationExpressionSyntax call,
            SemanticModel model,
            HashSet<ISymbol> path,
            AuthoredMember? context,
            EnumerationProof proof)
        {
            if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
                || method.MethodKind == MethodKind.DelegateInvoke)
            {
                return false;
            }

            if (IsFrameworkMaterialization(method))
            {
                return true;
            }

            AuthoredMember? target = Resolve(method, model.Compilation);

            if (target is not null)
            {
                // A compiler-generated iterator: its body is the enumerator, traversed where the call is made.
                if (IsIteratorMember(target) && IsSequenceOrEnumeratorType(method.ReturnType))
                {
                    return true;
                }

                if (!proof.AuthoredCalls.Add(MemberIdentity(target)))
                {
                    return false;
                }

                try
                {
                    ExpressionSyntax[] returned = ReturnedExpressions(target.Syntax);

                    // The caller's bindings are valid only for a call written in the caller's own syntax tree: a
                    // value followed through a field or property initializer comes from another file.
                    AuthoredMember calleeContext = context is not null && context.Model.SyntaxTree == call.SyntaxTree
                        ? BindInvocationContext(context, call, target)
                        : target;

                    return returned.Length != 0
                        && returned.All(value => ProveEnumerationLeaf(value, target.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), calleeContext, proof));
                }
                finally
                {
                    proof.AuthoredCalls.Remove(MemberIdentity(target));
                }
            }

            if (HasAuthoredTarget(method) || method.Locations.Any(static location => location.IsInSource))
            {
                return false;
            }

            // A framework or external member builds its own value, or wraps or passes through the sequences it
            // is given. Only a sequence interface or a reviewed wrapper is followed: a concrete type from outside
            // the closed set keeps its own members, which the caller then judges by that type.
            if (!IsFrameworkSequenceReturn(method.ReturnType))
            {
                return null;
            }

            // Only a reviewed operator builds a new sequence out of nothing but the sequences, and the callback
            // results, it is given. A framework call that returns an element or a stored value (First, ElementAt, a
            // service lookup, a volatile read) can hand back a sequence the proof never saw, so it is unproven.
            if (!IsReviewedSequenceOperator(method))
            {
                return false;
            }

            if (model.GetOperation(call) is not IInvocationOperation invocation)
            {
                return false;
            }

            List<IOperation> exposed = [];

            if (invocation.Instance is not null)
            {
                exposed.Add(invocation.Instance);
            }

            exposed.AddRange(invocation.Arguments.Select(static argument => argument.Value));

            foreach (IOperation value in exposed)
            {
                if (value.Syntax is not ExpressionSyntax valueSyntax
                    || valueSyntax == call
                    || model.GetTypeInfo(valueSyntax).Type is not { } valueType
                    || !IsEnumerableValueType(valueType)
                    || IsClosedFrameworkEnumerationType(valueType))
                {
                    continue;
                }

                // The sequence the call was given is enumerated by framework code inside the value it returns, through
                // the given sequence's own members, not through the slots of this operand. It must be proved, and
                // what it can be is judged on its own: a framework type must be a closed implementer, and an authored
                // one is the framework-member rule's concern at the call.
                EnumerationProof exposure = new() { RemainingWork = proof.RemainingWork };

                bool proved = ProveEnumerationLeaf(valueSyntax, model, path, context, exposure);

                proof.RemainingWork = exposure.RemainingWork;

                if (!proved || !HasAcceptableExposedCandidates(exposure))
                {
                    return false;
                }
            }

            // A callback result that the operator enumerates (the sequence SelectMany flattens) is a sequence the
            // value it returns will enumerate, so what the callback returns is proved the same way.
            if (EnumeratesCallbackResult(method))
            {
                foreach (IArgumentOperation argument in invocation.Arguments.Where(static argument => IsSequenceReturningDelegate(argument.Parameter?.Type)))
                {
                    EnumerationProof results = new() { RemainingWork = proof.RemainingWork };

                    bool proved = ProveCallbackResults(argument.Value, model, context, results);

                    proof.RemainingWork = results.RemainingWork;

                    if (!proved || !HasAcceptableExposedCandidates(results))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool HasAcceptableExposedCandidates(EnumerationProof exposure) =>
            !exposure.Candidates.Any(static candidate => !candidate.Locations.Any(static location => location.IsInSource)
                && !IsClosedEnumerationImplementer(candidate));

        // The framework operators that build a new sequence from the sequences (and callback results) they are given and
        // return neither an element nor a stored value of what they were given. Nothing outside this table is
        // followed through: an operator that returns an element, such as First or ElementAt, is not here.
        private static readonly IReadOnlySet<string> ReviewedSequenceOperators = new HashSet<string>(StringComparer.Ordinal)
        {
            "Append",
            "AsEnumerable",
            "Cast",
            "Chunk",
            "Concat",
            "DefaultIfEmpty",
            "Distinct",
            "DistinctBy",
            "Empty",
            "Except",
            "ExceptBy",
            "GroupBy",
            "GroupJoin",
            "Intersect",
            "IntersectBy",
            "Join",
            "OfType",
            "Order",
            "OrderBy",
            "OrderByDescending",
            "OrderDescending",
            "Prepend",
            "Range",
            "Repeat",
            "Reverse",
            "Select",
            "SelectMany",
            "Skip",
            "SkipLast",
            "SkipWhile",
            "Take",
            "TakeLast",
            "TakeWhile",
            "ThenBy",
            "ThenByDescending",
            "Union",
            "UnionBy",
            "Where",
            "Zip",
        };

        private static bool IsReviewedSequenceOperator(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            if (IsExactFrameworkType(definition.ContainingType, "System.Linq.Enumerable", typeof(Enumerable).Assembly.GetName()))
            {
                return ReviewedSequenceOperators.Contains(definition.Name);
            }

            if (IsExactFrameworkType(definition.ContainingType, "System.Linq.ImmutableArrayExtensions", typeof(System.Linq.ImmutableArrayExtensions).Assembly.GetName()))
            {
                return definition.Name is "Select" or "SelectMany" or "Where";
            }

            // The one asynchronous wrapper the call classification reviews: the other wrappers of an asynchronous
            // sequence are unclassified at their own call, so they are never followed here.
            return IsExactFrameworkType(definition.ContainingType, "System.Threading.Tasks.TaskAsyncEnumerableExtensions", typeof(System.Threading.Tasks.TaskAsyncEnumerableExtensions).Assembly.GetName())
                && definition.Name == "ConfigureAwait";
        }

        // The one framework family that enumerates a sequence its callback returns. Every other member that takes a
        // callback runs it for its own result, and what it returns is not enumerated by the member.
        private static bool EnumeratesCallbackResult(IMethodSymbol method)
        {
            IMethodSymbol definition = method.ReducedFrom ?? method;

            return definition.Name == "SelectMany"
                && (IsExactFrameworkType(definition.ContainingType, "System.Linq.Enumerable", typeof(Enumerable).Assembly.GetName())
                    || IsExactFrameworkType(definition.ContainingType, "System.Linq.ImmutableArrayExtensions", typeof(System.Linq.ImmutableArrayExtensions).Assembly.GetName()));
        }

        private static bool IsSequenceReturningDelegate(ITypeSymbol? type) =>
            type is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke }
            && IsEnumerableValueType(invoke.ReturnType);

        // The values a callback argument returns, proved the way an operand is. A lambda or anonymous method is read in
        // the tree that holds it, a method group through the authored method it names; a callback whose returns cannot
        // be found, or a framework method, leaves them unproven.
        private bool ProveCallbackResults(IOperation delegateValue, SemanticModel model, AuthoredMember? context, EnumerationProof results)
        {
            ExpressionSyntax[] values;

            SemanticModel valueModel = model;

            AuthoredMember? valueContext = context;

            switch (delegateValue.Syntax)
            {
                case AnonymousFunctionExpressionSyntax { ExpressionBody: { } expressionBody }:
                    values = [expressionBody];

                    break;

                case AnonymousFunctionExpressionSyntax { Block: { } block }:
                    values = [.. block.DescendantNodes(static node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                        .OfType<ReturnStatementSyntax>()
                        .Select(static returned => returned.Expression)
                        .OfType<ExpressionSyntax>()];

                    break;

                case ExpressionSyntax group when model.GetSymbolInfo(group).Symbol is IMethodSymbol named && Resolve(named, model.Compilation) is { } target:
                    values = ReturnedExpressions(target.Syntax);

                    valueModel = target.Model;

                    valueContext = target;

                    break;

                default:
                    return false;
            }

            return values.Length != 0
                && values.All(value => ProveEnumerationLeaf(value, valueModel, new HashSet<ISymbol>(SymbolEqualityComparer.Default), valueContext, results));
        }

        // A framework collection or enumerator interface, or the reviewed ConfigureAwait wrapper over an async
        // sequence. A value of such a type that a framework member returns is built by framework code from the
        // sequences it is given.
        private static bool IsFrameworkSequenceReturn(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named || named.Locations.Any(static location => location.IsInSource))
            {
                return false;
            }

            string key = TypeKey(named);

            if (key == "System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable`1")
            {
                return IsExactFrameworkType(named, key, typeof(System.Runtime.CompilerServices.ConfiguredCancelableAsyncEnumerable<>).Assembly.GetName());
            }

            return IsPremiseCoveredEnumerationInterface(named) || named.TypeKind == TypeKind.Interface && IsSequenceOrEnumeratorType(named)
                && FrameworkAssemblyIdentityMatches(named.ContainingAssembly.Identity, typeof(IEnumerable<>).Assembly.GetName());
        }

        private static bool IsAsyncSequence(ITypeSymbol type) => TypeKey(type) == "System.Collections.Generic.IAsyncEnumerable`1";

        // An iterator method that returns an enumerator is as lazy as one that returns a sequence: its body runs on the
        // MoveNext calls that follow the call, not at the call.
        private static bool IsLazyEnumerator(ITypeSymbol type) =>
            TypeKey(type) is "System.Collections.IEnumerator" or "System.Collections.Generic.IEnumerator`1" or "System.Collections.Generic.IAsyncEnumerator`1";

        private bool OwnsLazyEnumeratorCall(SyntaxNode call) => ownedLazyEnumeratorCalls.Contains(call);

        // A lazy sequence consumed only through an enumerator that a using declaration or statement owns. Its body
        // runs between the declaration and the end of that scope, where the enumerator is disposed, so the Work live
        // at the end of the scope is what the body ran under. Any other use of the enumerator than MoveNext,
        // MoveNextAsync and Current (passing it, storing it, returning it, disposing it by hand, capturing it in a
        // callable) leaves the consumption unproven.
        private (SyntaxNode Site, int Position)? ScopedEnumeratorConsumption(AuthoredMember member, InvocationExpressionSyntax source)
        {
            if (source.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetAsyncEnumerator" or "GetEnumerator" } access
                || access.Expression != source
                || access.Parent is not InvocationExpressionSyntax getCall
                || member.Model.GetSymbolInfo(getCall).Symbol is not IMethodSymbol getter
                || TypeKey(getter.ContainingType) is not ("System.Collections.Generic.IAsyncEnumerable`1" or "System.Collections.Generic.IEnumerable`1")
                || getCall.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
                || declarator.Parent is not VariableDeclarationSyntax { Variables.Count: 1 } declaration
                || member.Model.GetDeclaredSymbol(declarator) is not ILocalSymbol enumerator)
            {
                return null;
            }

            SyntaxNode site;

            SyntaxNode region;

            if (declaration.Parent is LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0, Parent: BlockSyntax block } statement)
            {
                site = statement;

                region = block;
            }
            else if (declaration.Parent is UsingStatementSyntax usingStatement && usingStatement.Declaration == declaration)
            {
                site = usingStatement;

                region = usingStatement.Statement;
            }
            else
            {
                return null;
            }

            foreach (IdentifierNameSyntax reference in region.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (reference.SpanStart < declarator.Span.End
                    || !SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(reference).Symbol, enumerator))
                {
                    continue;
                }

                if (reference.Parent is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MoveNextAsync" or "MoveNext" or "Current" } use
                    || use.Expression != reference
                    || use.Name.Identifier.ValueText != "Current" && use.Parent is not InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 }
                    || reference.Ancestors().TakeWhile(ancestor => ancestor != region).Any(static ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                {
                    return null;
                }
            }

            return (site, DisposalPosition(declaration));
        }

        // The same member of the same construction of the same framework or authored contract, whichever compilation's
        // symbol universe the two symbols came from: a value followed through a caller in another project carries
        // its own symbols, and Roslyn does not equate symbols of different compilations.
        private static bool SameEnumerationSlot(ISymbol left, ISymbol right) =>
            SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition)
            || left.Kind == right.Kind
                && left.Name == right.Name
                && left.ContainingType is { } leftType
                && right.ContainingType is { } rightType
                && leftType.OriginalDefinition.ToDisplayString() == rightType.OriginalDefinition.ToDisplayString()
                && leftType.ContainingAssembly?.Identity.Name == rightType.ContainingAssembly?.Identity.Name
                && (left is not IMethodSymbol leftMethod || right is IMethodSymbol rightMethod && leftMethod.Parameters.Length == rightMethod.Parameters.Length);

        private static bool IsSameEnumerationType(ITypeSymbol left, ITypeSymbol right) =>
            SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition)
            || left.OriginalDefinition.ToDisplayString() == right.OriginalDefinition.ToDisplayString()
                && left.ContainingAssembly?.Identity.Name == right.ContainingAssembly?.Identity.Name;

        private static bool DerivesFromEnumerationContainer(ITypeSymbol runtimeType, ITypeSymbol container)
        {
            for (ITypeSymbol? type = runtimeType; type is not null; type = type.BaseType)
            {
                if (IsSameEnumerationType(type, container))
                {
                    return true;
                }
            }

            return false;
        }

        // The implementation the runtime type selects for a slot the compiler bound statically.
        private static ISymbol? EnumerationImplementation(ITypeSymbol runtimeType, ISymbol slot)
        {
            if (slot.ContainingType.TypeKind == TypeKind.Interface)
            {
                if (runtimeType.FindImplementationForInterfaceMember(slot) is { } direct)
                {
                    return direct;
                }

                foreach (INamedTypeSymbol contract in runtimeType.AllInterfaces)
                {
                    if (contract.ToDisplayString() != slot.ContainingType.ToDisplayString()
                        || contract.ContainingAssembly?.Identity.Name != slot.ContainingType.ContainingAssembly?.Identity.Name)
                    {
                        continue;
                    }

                    foreach (ISymbol member in contract.GetMembers(slot.Name))
                    {
                        if (SameEnumerationSlot(member, slot) && runtimeType.FindImplementationForInterfaceMember(member) is { } found)
                        {
                            return found;
                        }
                    }
                }

                return null;
            }

            // An instance member belongs to the type that declares it and to the types derived from it. A candidate
            // that is neither (a value reached through an unmodeled conversion, say) has no such member to run.
            if (!slot.IsStatic && !DerivesFromEnumerationContainer(runtimeType, slot.ContainingType))
            {
                return null;
            }

            if (!slot.IsVirtual && !slot.IsAbstract && !slot.IsOverride)
            {
                return slot;
            }

            for (ITypeSymbol? type = runtimeType; type is not null; type = type.BaseType)
            {
                foreach (ISymbol candidate in type.GetMembers(slot.Name))
                {
                    if (candidate.Kind != slot.Kind)
                    {
                        continue;
                    }

                    ISymbol? overridden = candidate;

                    while (overridden is not null)
                    {
                        if (SameEnumerationSlot(overridden, slot))
                        {
                            return candidate;
                        }

                        overridden = overridden switch
                        {
                            IMethodSymbol method => method.OverriddenMethod,
                            IPropertySymbol property => property.OverriddenProperty,
                            _ => null,
                        };
                    }
                }
            }

            return slot;
        }

        private AuthoredMember? EnumerationAuthoredMember(ISymbol? implementation, Compilation compilation)
        {
            IMethodSymbol? method = implementation switch
            {
                IMethodSymbol direct => direct,
                IPropertySymbol property => property.GetMethod,
                _ => null,
            };

            return method is null ? null : Resolve(method, compilation);
        }

        private static bool HasEnumerationBody(AuthoredMember target) =>
            target.Syntax is not AccessorDeclarationSyntax { Body: null, ExpressionBody: null }
            && target.Syntax is not MethodDeclarationSyntax { Body: null, ExpressionBody: null };

        // The collection interfaces whose members spread lowering and enumerating framework members call instead of
        // the enumerator: Count and CopyTo to size and fill a result, Contains and the indexer to answer a query
        // without walking the sequence. The members of these contracts that change the collection are never called
        // by an operator that only reads it.
        private static readonly IReadOnlySet<string> CollectionContracts = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Collections.ICollection",
            "System.Collections.IList",
            "System.Collections.IDictionary",
            "System.Collections.Generic.ICollection`1",
            "System.Collections.Generic.IList`1",
            "System.Collections.Generic.ISet`1",
            "System.Collections.Generic.IDictionary`2",
            "System.Collections.Generic.IReadOnlyCollection`1",
            "System.Collections.Generic.IReadOnlyList`1",
            "System.Collections.Generic.IReadOnlySet`1",
            "System.Collections.Generic.IReadOnlyDictionary`2",
        };

        private static readonly IReadOnlySet<string> CollectionMutators = new HashSet<string>(StringComparer.Ordinal)
        {
            "Add",
            "Clear",
            "Insert",
            "Remove",
            "RemoveAt",
            "ExceptWith",
            "IntersectWith",
            "SymmetricExceptWith",
            "UnionWith",
        };

        // The authored implementations of the read members of every collection interface the exact candidate
        // implements. A member without source is a framework member, which the candidate's framework base type owns.
        private IEnumerable<AuthoredMember> AuthoredCollectionMembers(INamedTypeSymbol candidate, Compilation compilation)
        {
            HashSet<ISymbol> seen = new(SymbolEqualityComparer.Default);

            foreach (INamedTypeSymbol contract in candidate.AllInterfaces)
            {
                if (!CollectionContracts.Contains(TypeKey(contract)) || contract.Locations.Any(static location => location.IsInSource))
                {
                    continue;
                }

                foreach (ISymbol contractMember in contract.GetMembers())
                {
                    IMethodSymbol? slot = contractMember switch
                    {
                        IMethodSymbol { MethodKind: MethodKind.Ordinary } method when !CollectionMutators.Contains(method.Name) => method,
                        IPropertySymbol { GetMethod: { } getter } => getter,
                        _ => null,
                    };

                    if (slot is null)
                    {
                        continue;
                    }

                    ISymbol? implementation = candidate.FindImplementationForInterfaceMember(contractMember is IPropertySymbol ? contractMember : slot);

                    AuthoredMember? target = EnumerationAuthoredMember(implementation, compilation);

                    if (target is not null && HasEnumerationBody(target) && seen.Add(target.Symbol))
                    {
                        yield return target;
                    }
                }
            }
        }

        // Resolves every authored member the site can execute. A candidate that cannot be followed to a
        // closed framework member or an authored body is listed as unresolved; nothing is assumed for it.
        // A spread and an enumerating framework member also run the read members of an authored collection.
        private EnumerationResolution ResolveEnumerationTargets(AuthoredMember member, EnumerationBinding binding, EnumerationClosure closure, bool collectionMembers = false)
        {
            List<EnumerationTarget> targets = [];

            List<string> unresolved = [];

            if (closure.Kind == EnumerationClosureKind.Unproven)
            {
                return new(targets, unresolved);
            }

            if (collectionMembers)
            {
                foreach (INamedTypeSymbol candidate in closure.Candidates.Where(static candidate => candidate.Locations.Any(static location => location.IsInSource)))
                {
                    foreach (AuthoredMember collectionMember in AuthoredCollectionMembers(candidate, member.Model.Compilation))
                    {
                        targets.Add(new("collection@" + Normalize(collectionMember.Symbol), collectionMember, EnumerationStepTiming.Entry));
                    }
                }
            }

            if (!binding.Bound || binding.GetEnumerator is not { } getEnumerator)
            {
                if (closure.Kind == EnumerationClosureKind.Candidates)
                {
                    // The compiler-selected members cannot be replayed at a position that hosts no statement (a field or
                    // property initializer). Every member the foreach pattern and the enumeration contracts could select on
                    // an exact candidate is followed instead, which is a superset of what the compiler selects.
                    foreach (INamedTypeSymbol candidate in closure.Candidates)
                    {
                        List<EnumerationBinding> alternatives = [.. ContractBindings(candidate, binding.Operand)];

                        if (PatternEnumerationBinding(candidate, binding.Operand, binding.Asynchronous) is { } pattern)
                        {
                            alternatives.Add(pattern);
                        }

                        if (!alternatives.Any(static alternative => alternative.Bound && alternative.GetEnumerator is not null))
                        {
                            unresolved.Add((binding.OperandType?.ToDisplayString() ?? "<unbound>") + ".GetEnumerator");

                            continue;
                        }

                        foreach (EnumerationBinding alternative in alternatives)
                        {
                            // An alternative the lookup could not complete would only reach this branch again.
                            if (!alternative.Bound || alternative.GetEnumerator is null)
                            {
                                continue;
                            }

                            EnumerationResolution resolved = ResolveEnumerationTargets(member, alternative, new(EnumerationClosureKind.Candidates, [candidate]), collectionMembers: false);

                            targets.AddRange(resolved.Targets.Where(resolvedTarget => targets.All(existing => existing.Step != resolvedTarget.Step)));

                            unresolved.AddRange(resolved.Unresolved);
                        }
                    }
                }

                return new(targets, unresolved);
            }

            // Follows an authored enumerator provider: its body runs at the loop, and the enumerator it returns is
            // proved so that MoveNext, Current and Dispose are resolved on the exact enumerator types.
            void FollowProvider(AuthoredMember provider)
            {
                if (!HasEnumerationBody(provider))
                {
                    unresolved.Add(Normalize(getEnumerator));

                    return;
                }

                if (IsIteratorMember(provider))
                {
                    targets.Add(new("lazy@" + Normalize(provider.Symbol), provider, EnumerationStepTiming.Lazy));

                    return;
                }

                ExpressionSyntax[] returned = ReturnedExpressions(provider.Syntax);

                SyntaxNode[] lazyIterators = [.. returned
                    .Select(static value => StripTransparentExpression(value))
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => provider.Model.GetSymbolInfo(call).Symbol is IMethodSymbol callee
                        && Resolve(callee, provider.Model.Compilation) is { } iterator
                        && IsIteratorMember(iterator)
                        && IsLazyEnumerator(callee.ReturnType))];

                targets.Add(new("enumerator@" + Normalize(provider.Symbol), provider, EnumerationStepTiming.Entry, lazyIterators.Length == 0 ? null : lazyIterators));

                EnumerationProof enumeratorProof = new();

                if (returned.Length == 0
                    || !returned.All(value => ProveEnumerationLeaf(value, provider.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), provider, enumeratorProof)))
                {
                    unresolved.Add(Normalize(binding.MoveNext!));

                    return;
                }

                foreach (INamedTypeSymbol enumerator in enumeratorProof.Candidates.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>())
                {
                    ResolveEnumeratorStep(member, binding.MoveNext, enumerator, "moveNext", EnumerationStepTiming.Iteration, targets, unresolved);

                    ResolveEnumeratorStep(member, binding.Current, enumerator, "current", EnumerationStepTiming.Iteration, targets, unresolved);

                    ResolveEnumeratorStep(member, binding.Dispose, enumerator, "dispose", EnumerationStepTiming.Exit, targets, unresolved);
                }
            }

            // An authored extension or static GetEnumerator is chosen by the operand's static type, so it runs whatever
            // the operand is at run time, a closed framework collection or a null included.
            IMethodSymbol selected = getEnumerator.ReducedFrom ?? getEnumerator;

            if (selected.IsStatic && Resolve(selected, member.Model.Compilation) is { } selectedProvider)
            {
                FollowProvider(selectedProvider);

                return new(targets, unresolved);
            }

            if (closure.Kind != EnumerationClosureKind.Candidates)
            {
                return new(targets, unresolved);
            }

            foreach (INamedTypeSymbol candidate in closure.Candidates)
            {
                ISymbol? enumeratorSlot = EnumerationImplementation(candidate, getEnumerator);

                AuthoredMember? enumeratorTarget = EnumerationAuthoredMember(enumeratorSlot, member.Model.Compilation);

                if (enumeratorSlot is null)
                {
                    unresolved.Add(Normalize(getEnumerator));

                    continue;
                }

                if (enumeratorTarget is null)
                {
                    // A framework implementation is classified only when the framework type that declares it is
                    // closed, or the candidate is itself the exact framework type that declares it; a name, or a
                    // base type, does not make an authored subclass the framework type.
                    if (!IsTrustedFrameworkEnumerationImplementation(candidate, enumeratorSlot))
                    {
                        unresolved.Add(Normalize(getEnumerator));
                    }

                    continue;
                }

                FollowProvider(enumeratorTarget);
            }

            return new(targets, unresolved);
        }

        private void ResolveEnumeratorStep(
            AuthoredMember member,
            ISymbol? slot,
            INamedTypeSymbol enumerator,
            string step,
            EnumerationStepTiming timing,
            List<EnumerationTarget> targets,
            List<string> unresolved)
        {
            if (slot is null)
            {
                return;
            }

            ISymbol? implementation = EnumerationImplementation(enumerator, slot);

            if (implementation is null)
            {
                // A pattern enumerator that does not implement IDisposable has no Dispose to run.
                if (step != "dispose" || slot.ContainingType.TypeKind != TypeKind.Interface)
                {
                    unresolved.Add(Normalize(slot));
                }

                return;
            }

            AuthoredMember? target = EnumerationAuthoredMember(implementation, member.Model.Compilation);

            if (target is not null)
            {
                if (HasEnumerationBody(target))
                {
                    targets.Add(new(step + "@" + Normalize(target.Symbol), target, timing));
                }

                return;
            }

            if (!IsTrustedFrameworkEnumerationImplementation(enumerator, implementation))
            {
                unresolved.Add(Normalize(slot));
            }
        }

        private TraversalEvidence TraverseEnumeration(
            AuthoredMember member,
            SyntaxNode node,
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

            EnumerationBinding binding = BindEnumeration(member, node);

            // Enumerating an EF query executes it: a database access that holds its reader from the first MoveNext to the
            // disposal, so the Work and effect frontiers must be live at the operand, at the back edge and at the end.
            if (IsEfQueryOperand(member, binding.Operand))
            {
                int queryEntry = binding.Operand.Span.End + 1;

                int queryBackEdge = node is CommonForEachStatementSyntax queryLoop ? Math.Max(queryEntry, queryLoop.Statement.Span.End) : queryEntry;

                int queryExit = node is CommonForEachStatementSyntax queryWhole ? Math.Max(queryBackEdge, queryWhole.Span.End) : queryEntry;

                int[] positions = [queryEntry, queryBackEdge, queryExit];

                string? queryWork = CommonFrontier(positions.Select(position => WorkAt(member, node, operationId, workKind, inheritedWork, position)));

                string? queryEffect = CommonFrontier(positions.Select(position => EffectAt(member, node, operationId, inheritedEffect, position)));

                string queryCallee = binding.GetEnumerator is { } queryEnumerator
                    ? Normalize(queryEnumerator)
                    : binding.Asynchronous
                        ? "System.Collections.Generic.IAsyncEnumerable`1.GetAsyncEnumerator"
                        : "System.Collections.Generic.IEnumerable`1.GetEnumerator";

                evidence = MergeEvidence(evidence, AddSite(HostedProducerSiteKind.DatabaseAccess, queryCallee, member, rootType, operationId, node, queryEffect, queryWork));

                return MergeEvidence(
                    evidence,
                    TraverseEfQueryCallbacks(member, node, [binding.Operand], null, queryCallee, rootType, operationId, active, lifecycle, queryWork, queryEffect, recoveryEffect));
            }

            EnumerationClosure closure = ClassifyEnumeration(member, binding);

            string site = operationId + "/enumerate@" + Location(node);

            if (closure.Kind == EnumerationClosureKind.Unproven)
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new(
                    "HOSTED_SITE_UNCLASSIFIED",
                    Location(node),
                    binding.GetEnumerator is { } slot
                        ? Normalize(slot)
                        : (binding.OperandType?.ToDisplayString() ?? "<unbound>") + ".GetEnumerator"));

                return evidence;
            }

            EnumerationResolution resolution = ResolveEnumerationTargets(member, binding, closure, collectionMembers: node is SpreadElementSyntax);

            foreach (string callee in resolution.Unresolved.Distinct(StringComparer.Ordinal))
            {
                RefuseTraversalCompletion();

                diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), callee));
            }

            // The position just past the operand: a call that ends exactly at the operand boundary has completed.
            int entry = binding.Operand.Span.End + 1;

            int backEdge = node is CommonForEachStatementSyntax loop ? Math.Max(entry, loop.Statement.Span.End) : entry;

            int exit = node is CommonForEachStatementSyntax whole ? Math.Max(backEdge, whole.Span.End) : entry;

            (string? Work, string? Effect) FrontierAt(int position) =>
                (WorkAt(member, node, operationId, workKind, inheritedWork, position),
                    EffectAt(member, node, operationId, inheritedEffect, position));

            // A frontier survives a span only if the same one is live at both of its ends, because disposal is
            // monotone in position: whatever ended before the later end has ended for everything between.
            (string? Work, string? Effect) Across(params int[] positions)
            {
                (string? Work, string? Effect) first = FrontierAt(positions[0]);

                foreach (int position in positions.Skip(1))
                {
                    (string? Work, string? Effect) next = FrontierAt(position);

                    if (next.Work != first.Work)
                    {
                        first.Work = null;
                    }

                    if (next.Effect != first.Effect)
                    {
                        first.Effect = null;
                    }
                }

                return first;
            }

            foreach (EnumerationTarget target in resolution.Targets)
            {
                (string? work, string? effect) = target.Timing switch
                {
                    EnumerationStepTiming.Entry => FrontierAt(entry),
                    EnumerationStepTiming.Iteration => Across(entry, backEdge),
                    EnumerationStepTiming.Exit => Across(entry, exit),
                    _ => Across(entry, backEdge, exit),
                };

                AuthoredMember bound = target.Member.Symbol.IsStatic && target.Member.Symbol.Parameters.Length >= 1
                    ? BindArguments(member, [(binding.Operand, target.Member.Symbol.Parameters[0], RefKind.None)], target.Member)
                    : target.Member;

                // An iterator method that an enumerator provider returns runs its body on the MoveNext calls that
                // follow, from the first until the loop ends. Its body is owned by the provider's traversal only when
                // the frontier live at the operand is the one live at every one of those points; otherwise the call
                // stays unowned, and is refused and traversed with no frontier.
                SyntaxNode[] ownedIterators = target.LazyIterators is { } lazyIterators && Across(entry, backEdge, exit) == FrontierAt(entry)
                    ? [.. lazyIterators]
                    : [];

                foreach (SyntaxNode ownedIterator in ownedIterators)
                {
                    ownedLazyEnumeratorCalls.Add(ownedIterator);
                }

                try
                {
                    evidence = MergeEvidence(
                        evidence,
                        Traverse(
                            bound,
                            rootType,
                            site + "/" + target.Step,
                            active,
                            lifecycle,
                            work,
                            effect,
                            completionOwned: binding.Asynchronous,
                            recoveryEffect: work is not null && effect is not null ? recoveryEffect : null));
                }
                finally
                {
                    foreach (SyntaxNode ownedIterator in ownedIterators)
                    {
                        ownedLazyEnumeratorCalls.Remove(ownedIterator);
                    }
                }
            }

            // The iteration variable of a deconstructing loop is deconstructed from each element at the loop head.
            if (binding.Deconstruction is { } deconstruction)
            {
                List<ImplicitCallStep> deconstructions = [];

                DeconstructionSteps(deconstruction, binding.ElementType, null, entry, deconstructions);

                (string? work, string? effect) = Across(entry, backEdge);

                foreach (ImplicitCallStep step in deconstructions)
                {
                    evidence = MergeEvidence(
                        evidence,
                        TraverseImplicitTargets(
                            member,
                            node,
                            ResolveImplicitStep(member, step),
                            [],
                            rootType,
                            operationId,
                            site + "/" + step.Label + "@" + Normalize(step.Method),
                            active,
                            lifecycle,
                            work,
                            effect,
                            work is not null && effect is not null ? recoveryEffect : null));
                }
            }

            // The asynchronous members of an await foreach return awaitables that the loop awaits: MoveNextAsync at the loop
            // head and DisposeAsync at the exit. The awaiter members of each are calls the compiler makes where the loop awaits.
            if (binding.Asynchronous && binding.Bound)
            {
                foreach ((ITypeSymbol? awaitable, string awaited, (string? Work, string? Effect) frontier) in new (ITypeSymbol?, string, (string?, string?))[]
                {
                    (binding.MoveNext?.ReturnType, "moveNext-await", Across(entry, backEdge)),
                    (binding.Dispose?.ReturnType, "dispose-await", Across(entry, exit)),
                })
                {
                    if (awaitable is null)
                    {
                        continue;
                    }

                    if (AwaiterSteps(member.Model, node, awaitable, entry) is not { } awaitSteps)
                    {
                        RefuseTraversalCompletion();

                        diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), awaitable.ToDisplayString() + ".GetAwaiter"));

                        continue;
                    }

                    foreach (ImplicitCallStep step in awaitSteps)
                    {
                        evidence = MergeEvidence(
                            evidence,
                            TraverseImplicitTargets(
                                member,
                                node,
                                ResolveImplicitStep(member, step),
                                [],
                                rootType,
                                operationId,
                                site + "/" + awaited + "/" + step.Label + "@" + Normalize(step.Method),
                                active,
                                lifecycle,
                                frontier.Work,
                                frontier.Effect,
                                frontier.Work is not null && frontier.Effect is not null ? recoveryEffect : null));
                    }
                }
            }

            foreach (IMethodSymbol conversion in binding.Conversions)
            {
                (string? work, string? effect) = Across(entry, backEdge);

                if (Resolve(conversion, member.Model.Compilation) is { } conversionTarget)
                {
                    evidence = MergeEvidence(
                        evidence,
                        Traverse(
                            conversionTarget,
                            rootType,
                            site + "/conversion@" + Normalize(conversion),
                            active,
                            lifecycle,
                            work,
                            effect,
                            recoveryEffect: work is not null && effect is not null ? recoveryEffect : null));
                }
                else if (!IsReviewedEffectFreeExternalMember(conversion, null, member.Model, member))
                {
                    RefuseTraversalCompletion();

                    diagnostics.Add(new("HOSTED_SITE_UNCLASSIFIED", Location(node), Normalize(conversion)));
                }
            }

            return evidence;
        }

        // Authored enumerators a framework member can run when it enumerates a sequence it was given. The sequence
        // is followed to its exact runtime types. A sequence that can be an authored type is refused unless every
        // enumeration member that type provides, and every read member of the collection interfaces it implements,
        // is free of producer sites by the same transitive scan an opaque callback body gets. A sequence whose
        // runtime type cannot be followed is refused too when the member declares a sequence parameter for it: the
        // member enumerates it, and nothing says that what it runs is not authored. The result a SelectMany-style
        // callback returns is a sequence the member enumerates, and is judged the same way.
        private bool FrameworkMemberRunsAuthoredEnumerator(AuthoredMember member, SyntaxNode node, IMethodSymbol method, out string detail)
        {
            detail = Normalize(method) + "; Opaque enumerable semantics cannot prove execution or completion ownership.";

            IEnumerable<IArgumentOperation> arguments = member.Model.GetOperation(node) switch
            {
                IInvocationOperation invocation => invocation.Arguments,
                IObjectCreationOperation creation => creation.Arguments,
                _ => [],
            };

            foreach (IArgumentOperation argument in arguments)
            {
                if (argument.Value.Syntax is not ExpressionSyntax value || value == node)
                {
                    continue;
                }

                // An EF query is no authored enumerator: its enumeration is a database access, classified at the call.
                if (IsEfQueryOperand(member, value))
                {
                    continue;
                }

                if (EnumeratesCallbackResult(method) && IsSequenceReturningDelegate(argument.Parameter?.Type))
                {
                    EnumerationProof results = new();

                    if (!ProveCallbackResults(argument.Value, member.Model, member, results)
                        || CandidatesRunProducerSite(member, value, null, results))
                    {
                        return true;
                    }

                    continue;
                }

                if (member.Model.GetTypeInfo(value).Type is not { } valueType)
                {
                    continue;
                }

                // A value whose static type hides that it is enumerable (object, an interface, an unsealed class, a
                // type parameter) can be any authored enumerable assignable to that type, and the framework member may
                // enumerate it whatever the parameter is declared as.
                if (!IsEnumerableValueType(valueType))
                {
                    if (HiddenEnumerableRunsProducerSite(member, value, valueType))
                    {
                        return true;
                    }

                    continue;
                }

                if (IsClosedFrameworkEnumerationType(valueType))
                {
                    continue;
                }

                EnumerationProof proof = new();

                if (!ProveEnumerationLeaf(value, member.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member, proof))
                {
                    if (argument.Parameter is { } parameter && IsEnumerableValueType(parameter.Type))
                    {
                        return true;
                    }

                    continue;
                }

                if (CandidatesRunProducerSite(member, value, valueType, proof))
                {
                    return true;
                }
            }

            return false;
        }

        // An authored class or struct that can be enumerated: it implements an enumeration contract or has a GetEnumerator
        // or GetAsyncEnumerator of its own. The keys are those of every type it is assignable to.
        private sealed record AuthoredEnumerable(INamedTypeSymbol Type, IReadOnlySet<string> AssignableKeys);

        private IReadOnlyList<AuthoredEnumerable>? authoredEnumerables;

        private IReadOnlyList<AuthoredEnumerable> AuthoredEnumerables() => authoredEnumerables ??=
        [
            .. analysedCompilations
                .SelectMany(static compilation => SourceTypes(compilation.Assembly.GlobalNamespace))
                .Where(static type => type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsAbstract && !type.IsStatic && !type.IsImplicitlyDeclared && type.Locations.Any(static location => location.IsInSource)
                    && (IsEnumerableValueType(type) || type.GetMembers().OfType<IMethodSymbol>().Any(static method => method.Name is "GetEnumerator" or "GetAsyncEnumerator")))
                .Select(static type => new AuthoredEnumerable(type, AssignableKeys(type))),
        ];

        // Whether any authored enumerable that can be the value runs a producer site when a framework member enumerates it.
        private bool HiddenEnumerableRunsProducerSite(AuthoredMember member, ExpressionSyntax value, ITypeSymbol valueType)
        {
            if (valueType.TypeKind is TypeKind.Struct or TypeKind.Enum or TypeKind.Delegate or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Array or TypeKind.Error
                || valueType.SpecialType == SpecialType.System_String
                || valueType is INamedTypeSymbol { IsSealed: true } && !valueType.Locations.Any(static location => location.IsInSource))
            {
                return false;
            }

            foreach (AuthoredEnumerable candidate in AuthoredEnumerables())
            {
                bool assignable = valueType.SpecialType == SpecialType.System_Object
                    || valueType.TypeKind is TypeKind.TypeParameter or TypeKind.Dynamic
                    || candidate.AssignableKeys.Contains(valueType.OriginalDefinition.ToDisplayString());

                if (!assignable)
                {
                    continue;
                }

                EnumerationProof proof = new();

                proof.Candidates.Add(candidate.Type);

                if (CandidatesRunProducerSite(member, value, null, proof))
                {
                    return true;
                }
            }

            return false;
        }

        private bool CandidatesRunProducerSite(AuthoredMember member, ExpressionSyntax value, ITypeSymbol? valueType, EnumerationProof proof)
        {
            INamedTypeSymbol[] candidates = [.. proof.Candidates
                .Distinct(SymbolEqualityComparer.Default)
                .OfType<INamedTypeSymbol>()];

            if (candidates.Length == 0)
            {
                return false;
            }

            // A candidate whose enumeration members cannot be followed to a closed framework member or an authored body
            // is as unproven as an operand nothing proves: a framework type outside the reviewed set, such as a
            // read-only view, forwards to whatever list it wraps.
            bool Refuses(EnumerationResolution resolution) =>
                resolution.Unresolved.Count != 0
                || resolution.Targets.Any(target => ContainsPotentialProducerSite(target.Member));

            // Count, CopyTo, Contains and the indexer run instead of the enumerator when the member can size or query
            // the collection without walking it.
            if (candidates.Any(candidate => AuthoredCollectionMembers(candidate, member.Model.Compilation).Any(collectionMember => ContainsPotentialProducerSite(collectionMember))))
            {
                return true;
            }

            // A sequence written as an argument is bound the way a loop over it would be. The result of a callback is
            // not a written operand: it is enumerated through the contract alone.
            if (valueType is not null)
            {
                bool asynchronous = valueType.AllInterfaces.Concat([valueType]).Any(static contract => TypeKey(contract) == "System.Collections.Generic.IAsyncEnumerable`1")
                    && !valueType.AllInterfaces.Concat([valueType]).Any(static contract => TypeKey(contract) is "System.Collections.Generic.IEnumerable`1" or "System.Collections.IEnumerable");

                EnumerationBinding binding = BindEnumerationExpression(member, value, value.SpanStart, asynchronous);

                if (Refuses(ResolveEnumerationTargets(member, binding, new(EnumerationClosureKind.Candidates, candidates))))
                {
                    return true;
                }
            }

            // The framework member enumerates through the contract, not through the pattern the compiler would bind for
            // a loop over the same value: a type may implement the contract explicitly, with a body of its own.
            foreach (INamedTypeSymbol candidate in candidates)
            {
                foreach (EnumerationBinding contractBinding in ContractBindings(candidate, value))
                {
                    if (Refuses(ResolveEnumerationTargets(member, contractBinding, new(EnumerationClosureKind.Candidates, [candidate]))))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static IMethodSymbol? PublicInstanceMethod(ITypeSymbol type, string name) =>
            BaseChain(type)
                .Concat(type.TypeKind == TypeKind.Interface ? type.AllInterfaces : [])
                .SelectMany(current => current.GetMembers(name).OfType<IMethodSymbol>())
                .FirstOrDefault(static method => !method.IsStatic && method.DeclaredAccessibility == Accessibility.Public && method.Parameters.All(static parameter => parameter.IsOptional));

        private static IEnumerable<ITypeSymbol> BaseChain(ITypeSymbol type)
        {
            for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                yield return current;
            }
        }

        // The foreach pattern replayed by member lookup on an exact type: a public instance GetEnumerator (or
        // GetAsyncEnumerator) and the MoveNext, Current and Dispose (or the asynchronous members) of what it returns.
        private static EnumerationBinding? PatternEnumerationBinding(INamedTypeSymbol candidate, ExpressionSyntax operand, bool asynchronous)
        {
            if (PublicInstanceMethod(candidate, asynchronous ? "GetAsyncEnumerator" : "GetEnumerator") is not { } getEnumerator)
            {
                return null;
            }

            ITypeSymbol enumerator = getEnumerator.ReturnType;

            IMethodSymbol? moveNext = PublicInstanceMethod(enumerator, asynchronous ? "MoveNextAsync" : "MoveNext");

            IPropertySymbol? current = BaseChain(enumerator).Concat(enumerator.AllInterfaces).SelectMany(static type => type.GetMembers("Current").OfType<IPropertySymbol>()).FirstOrDefault(static property => !property.IsStatic);

            IMethodSymbol? dispose = PublicInstanceMethod(enumerator, asynchronous ? "DisposeAsync" : "Dispose")
                ?? enumerator.AllInterfaces.SelectMany(static type => type.GetMembers().OfType<IMethodSymbol>()).FirstOrDefault(method => method.Name == (asynchronous ? "DisposeAsync" : "Dispose") && method.Parameters.Length == 0);

            return new(operand, candidate, asynchronous, getEnumerator, moveNext, current, dispose, [], moveNext is not null && current is not null);
        }

        // The bindings of the enumeration contracts a type implements, taken from the type's own interface symbols so
        // that they belong to the same compilation as the type: GetEnumerator of IEnumerable<T> and IEnumerable with
        // MoveNext, Current and Dispose of the enumerators they return, and GetAsyncEnumerator of IAsyncEnumerable<T>
        // with MoveNextAsync, Current and DisposeAsync.
        private static IEnumerable<EnumerationBinding> ContractBindings(INamedTypeSymbol candidate, ExpressionSyntax operand)
        {
            foreach (INamedTypeSymbol contract in candidate.AllInterfaces)
            {
                bool asynchronous = TypeKey(contract) == "System.Collections.Generic.IAsyncEnumerable`1";

                if (!asynchronous && TypeKey(contract) is not ("System.Collections.Generic.IEnumerable`1" or "System.Collections.IEnumerable"))
                {
                    continue;
                }

                IMethodSymbol? getEnumerator = contract.GetMembers(asynchronous ? "GetAsyncEnumerator" : "GetEnumerator").OfType<IMethodSymbol>().FirstOrDefault();

                if (getEnumerator?.ReturnType is not INamedTypeSymbol enumerator)
                {
                    continue;
                }

                INamedTypeSymbol[] enumeratorContracts = [enumerator, .. enumerator.AllInterfaces];

                IMethodSymbol? moveNext = enumeratorContracts
                    .Where(item => TypeKey(item) == (asynchronous ? "System.Collections.Generic.IAsyncEnumerator`1" : "System.Collections.IEnumerator"))
                    .SelectMany(static item => item.GetMembers().OfType<IMethodSymbol>())
                    .FirstOrDefault(static method => method.Name is "MoveNext" or "MoveNextAsync");

                IMethodSymbol? dispose = enumeratorContracts
                    .Where(item => TypeKey(item) == (asynchronous ? "System.IAsyncDisposable" : "System.IDisposable"))
                    .SelectMany(static item => item.GetMembers().OfType<IMethodSymbol>())
                    .FirstOrDefault(static method => method.Name is "Dispose" or "DisposeAsync");

                IPropertySymbol[] currents = [.. enumeratorContracts
                    .Where(item => TypeKey(item) is "System.Collections.IEnumerator" or "System.Collections.Generic.IEnumerator`1" or "System.Collections.Generic.IAsyncEnumerator`1")
                    .SelectMany(static item => item.GetMembers("Current").OfType<IPropertySymbol>())];

                foreach (IPropertySymbol current in currents.Length == 0 ? [] : currents)
                {
                    yield return new(operand, candidate, asynchronous, getEnumerator, moveNext, current, dispose, [], moveNext is not null);
                }
            }
        }

        // The authored members a foreach or spread inside a scanned body can run. Used by the transitive producer
        // scan so that a callback body is not judged effect-free while an authored enumerator hides in its loops.
        private bool EnumerationReachesProducerSite(AuthoredMember member, SyntaxNode node, HashSet<GraphMemberIdentity> path)
        {
            EnumerationBinding binding = BindEnumeration(member, node);

            EnumerationClosure closure = ClassifyEnumeration(member, binding);

            // An operand nothing proves can run any enumerator, so a body that enumerates it can reach a producer site.
            if (closure.Kind == EnumerationClosureKind.Unproven)
            {
                return !scanSitesOnly;
            }

            EnumerationResolution resolution = ResolveEnumerationTargets(member, binding, closure, collectionMembers: node is SpreadElementSyntax);

            if (!scanSitesOnly && resolution.Unresolved.Count != 0 || resolution.Targets.Any(target => ContainsPotentialProducerSite(target.Member, path)))
            {
                return true;
            }

            // The awaiter members of the awaitables an await foreach awaits.
            if (binding.Asynchronous && binding.Bound)
            {
                foreach (ITypeSymbol? awaitable in new[] { binding.MoveNext?.ReturnType, binding.Dispose?.ReturnType })
                {
                    if (awaitable is null)
                    {
                        continue;
                    }

                    if (AwaiterSteps(member.Model, node, awaitable, binding.Operand.Span.End + 1) is not { } awaitSteps)
                    {
                        return !scanSitesOnly;
                    }

                    foreach (ImplicitCallStep step in awaitSteps)
                    {
                        ImplicitTargets awaiting = ResolveImplicitStep(member, step);

                        if (ImplicitTargetsReachProducerSite(member, awaiting, node, path))
                        {
                            return true;
                        }
                    }
                }
            }

            if (binding.Deconstruction is not { } deconstruction)
            {
                return false;
            }

            List<ImplicitCallStep> deconstructions = [];

            DeconstructionSteps(deconstruction, binding.ElementType, null, binding.Operand.Span.End + 1, deconstructions);

            return deconstructions.Any(step => ImplicitTargetsReachProducerSite(member, ResolveImplicitStep(member, step), node, path));
        }
    }
}
