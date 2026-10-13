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

        return anchors;
    }

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
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
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
                    if (!method.IsImplicitlyDeclared
                        && (method.Name.EndsWith("GetEnumerator", StringComparison.Ordinal)
                            || method.Name.EndsWith("GetAsyncEnumerator", StringComparison.Ordinal)))
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
            bool Bound);

        private sealed record EnumerationTarget(string Step, AuthoredMember Member, EnumerationStepTiming Timing);

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
            IReadOnlyList<string> Unresolved,
            bool AuthoredCandidate);

        // A binding depends only on the syntax and its semantic model, never on a traversal state.
        private readonly Dictionary<SyntaxNode, EnumerationBinding> enumerationBindings = new(ReferenceEqualityComparer.Instance);

        private EnumerationBinding BindEnumeration(AuthoredMember member, SyntaxNode node)
        {
            if (enumerationBindings.TryGetValue(node, out EnumerationBinding? cached))
            {
                return cached;
            }

            EnumerationBinding binding = BindEnumerationUncached(member, node);

            enumerationBindings[node] = binding;

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
                    info.GetEnumeratorMethod is not null && info.MoveNextMethod is not null && info.CurrentProperty is not null);
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

            if (statement is not CommonForEachStatementSyntax synthetic
                || !member.Model.TryGetSpeculativeSemanticModel(position, statement, out SemanticModel? speculative)
                || speculative is null)
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
            if (!binding.Bound || binding.OperandType is not { } type || type.TypeKind is TypeKind.Error or TypeKind.Dynamic)
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
            if (proof.RemainingWork-- <= 0)
            {
                return false;
            }

            expression = StripTransparentExpression(expression);

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
                case DefaultExpressionSyntax:
                    return true;

                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NullLiteralExpression) || literal.IsKind(SyntaxKind.DefaultLiteralExpression):
                    return true;

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

            if (IsClosedFrameworkEnumerationType(type))
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

            if (type is INamedTypeSymbol exact
                && (exact.TypeKind == TypeKind.Struct || exact is { TypeKind: TypeKind.Class, IsSealed: true }))
            {
                proof.Candidates.Add(exact);

                return true;
            }

            if (HasOnlyReviewedValueSources(
                expression,
                model,
                path,
                context,
                (source, sourceModel, sourcePath, sourceContext) => ProveEnumerationLeaf(source, sourceModel, sourcePath, sourceContext, proof)))
            {
                return true;
            }

            // Authored Enumerables Absent: the storage could not be followed, but no authored type provides
            // enumeration members, so a value of a framework collection interface is not an authored one.
            return AuthoredEnumerablesAbsentByUse && IsPremiseCoveredEnumerationInterface(type);
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

                if (!ProveEnumerationLeaf(valueSyntax, model, path, context, proof))
                {
                    return false;
                }
            }

            return true;
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

        // The implementation the runtime type selects for a slot the compiler bound statically.
        private static ISymbol? EnumerationImplementation(ITypeSymbol runtimeType, ISymbol slot)
        {
            if (slot.ContainingType.TypeKind == TypeKind.Interface)
            {
                return runtimeType.FindImplementationForInterfaceMember(slot);
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
                        if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, slot.OriginalDefinition))
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

        // Resolves every authored member the site can execute. A candidate that cannot be followed to a
        // closed framework member or an authored body is listed as unresolved; nothing is assumed for it.
        private EnumerationResolution ResolveEnumerationTargets(AuthoredMember member, EnumerationBinding binding, EnumerationClosure closure)
        {
            List<EnumerationTarget> targets = [];

            List<string> unresolved = [];

            bool authoredCandidate = false;

            if (closure.Kind != EnumerationClosureKind.Candidates || binding.GetEnumerator is not { } getEnumerator)
            {
                return new(targets, unresolved, false);
            }

            foreach (INamedTypeSymbol candidate in closure.Candidates)
            {
                authoredCandidate |= candidate.Locations.Any(static location => location.IsInSource);

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
                    // closed; what the candidate type is named, or derives from, does not matter.
                    if (!IsClosedFrameworkEnumerationType(enumeratorSlot.ContainingType))
                    {
                        unresolved.Add(Normalize(getEnumerator));
                    }

                    continue;
                }

                if (!HasEnumerationBody(enumeratorTarget))
                {
                    unresolved.Add(Normalize(getEnumerator));

                    continue;
                }

                if (IsIteratorMember(enumeratorTarget))
                {
                    targets.Add(new("lazy@" + Normalize(enumeratorTarget.Symbol), enumeratorTarget, EnumerationStepTiming.Lazy));

                    continue;
                }

                targets.Add(new("enumerator@" + Normalize(enumeratorTarget.Symbol), enumeratorTarget, EnumerationStepTiming.Entry));

                EnumerationProof enumeratorProof = new();

                ExpressionSyntax[] returned = ReturnedExpressions(enumeratorTarget.Syntax);

                if (returned.Length == 0
                    || !returned.All(value => ProveEnumerationLeaf(value, enumeratorTarget.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), enumeratorTarget, enumeratorProof)))
                {
                    unresolved.Add(Normalize(binding.MoveNext!));

                    continue;
                }

                foreach (INamedTypeSymbol enumerator in enumeratorProof.Candidates.Distinct(SymbolEqualityComparer.Default).OfType<INamedTypeSymbol>())
                {
                    ResolveEnumeratorStep(member, binding.MoveNext, enumerator, "moveNext", EnumerationStepTiming.Iteration, targets, unresolved);

                    ResolveEnumeratorStep(member, binding.Current, enumerator, "current", EnumerationStepTiming.Iteration, targets, unresolved);

                    ResolveEnumeratorStep(member, binding.Dispose, enumerator, "dispose", EnumerationStepTiming.Exit, targets, unresolved);
                }
            }

            return new(targets, unresolved, authoredCandidate);
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

            if (!IsClosedFrameworkEnumerationType(implementation.ContainingType))
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

            EnumerationClosure closure = ClassifyEnumeration(member, binding);

            string site = operationId + "/enumerate@" + Location(node);

            if (!binding.Bound || closure.Kind == EnumerationClosureKind.Unproven)
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

            EnumerationResolution resolution = ResolveEnumerationTargets(member, binding, closure);

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
        // is followed to its exact runtime types; one whose runtime type cannot be followed is not claimed here (the
        // member's own site classification stands). A sequence that can be an authored type is refused unless every
        // enumeration member that type provides is free of producer sites by the same transitive scan an opaque
        // callback body gets.
        private bool FrameworkMemberRunsAuthoredEnumerator(AuthoredMember member, SyntaxNode node, IMethodSymbol method, out string detail)
        {
            detail = string.Empty;

            IEnumerable<IArgumentOperation> arguments = member.Model.GetOperation(node) switch
            {
                IInvocationOperation invocation => invocation.Arguments,
                IObjectCreationOperation creation => creation.Arguments,
                _ => [],
            };

            foreach (IArgumentOperation argument in arguments)
            {
                if (argument.Value.Syntax is not ExpressionSyntax value
                    || value == node
                    || member.Model.GetTypeInfo(value).Type is not { } valueType
                    || !IsEnumerableValueType(valueType)
                    || IsClosedFrameworkEnumerationType(valueType))
                {
                    continue;
                }

                EnumerationProof proof = new();

                if (!ProveEnumerationLeaf(value, member.Model, new HashSet<ISymbol>(SymbolEqualityComparer.Default), member, proof))
                {
                    continue;
                }

                INamedTypeSymbol[] candidates = [.. proof.Candidates
                    .Distinct(SymbolEqualityComparer.Default)
                    .OfType<INamedTypeSymbol>()
                    .Where(static candidate => candidate.Locations.Any(static location => location.IsInSource))];

                if (candidates.Length == 0)
                {
                    continue;
                }

                bool asynchronous = valueType.AllInterfaces.Concat([valueType]).Any(static contract => TypeKey(contract) == "System.Collections.Generic.IAsyncEnumerable`1")
                    && !valueType.AllInterfaces.Concat([valueType]).Any(static contract => TypeKey(contract) is "System.Collections.Generic.IEnumerable`1" or "System.Collections.IEnumerable");

                EnumerationBinding binding = BindEnumerationExpression(member, value, value.SpanStart, asynchronous);

                EnumerationResolution resolution = binding.Bound
                    ? ResolveEnumerationTargets(member, binding, new(EnumerationClosureKind.Candidates, candidates))
                    : new([], ["<unbound>"], true);

                if (resolution.AuthoredCandidate
                    && (resolution.Unresolved.Count != 0
                        || resolution.Targets.Any(target => ContainsPotentialProducerSite(target.Member))))
                {
                    detail = Normalize(method) + "; Opaque enumerable semantics cannot prove execution or completion ownership.";

                    return true;
                }
            }

            return false;
        }

        // The authored members a foreach or spread inside a scanned body can run. Used by the transitive producer
        // scan so that a callback body is not judged effect-free while an authored enumerator hides in its loops.
        private bool EnumerationReachesProducerSite(AuthoredMember member, SyntaxNode node, HashSet<GraphMemberIdentity> path)
        {
            EnumerationBinding binding = BindEnumeration(member, node);

            if (!binding.Bound)
            {
                return false;
            }

            EnumerationClosure closure = ClassifyEnumeration(member, binding);

            if (closure.Kind != EnumerationClosureKind.Candidates)
            {
                return false;
            }

            EnumerationResolution resolution = ResolveEnumerationTargets(member, binding, closure);

            return resolution.Targets.Any(target => ContainsPotentialProducerSite(target.Member, path));
        }
    }
}
