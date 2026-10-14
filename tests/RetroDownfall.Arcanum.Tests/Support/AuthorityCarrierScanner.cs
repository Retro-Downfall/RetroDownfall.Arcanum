using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>One place a gate-minted authority handle can be kept, captured or lent beyond the parameter that carried it.</summary>
/// <param name="Kind">field, property, primary-constructor, closure, delegate-receiver, store, ref-store, sink, service or bind.</param>
/// <param name="Holder">The fully qualified holder: a member, a primary-constructor parameter, or the member that contains the closure or the call.</param>
/// <param name="Authority">The authority interface the holder keeps.</param>
internal readonly record struct AuthorityCarrier(string Kind, string Holder, string Authority)
{
    public override string ToString() => Kind + "|" + Holder + "|" + Authority;
}

/// <summary>
/// The Authority Carrier Ban: nothing outside the admission gate stores, captures or lends a gate-minted authority handle.
/// </summary>
/// <remarks>
/// Carried Admission passes one work lease by value from its mint to every use, so the lease and the external-effect
/// group begun on it never live in a field, a property, an event, a primary-constructor capture, a closure, a delegate
/// bound to them or a heap location written from them. The scan is semantic: it follows a handle through locals, patterns,
/// loop and deconstruction variables, element reads, return values, supertypes (<c>object</c>, <c>IAsyncDisposable</c>, a type
/// parameter) and through the authored callees it is handed to (every override and implementation of a virtual or interface
/// member, across compilations), and it refuses a handle given to a callee it cannot read.
///
/// <para>A callee, member or type is judged by what its declaration says, not by what a call site closed it over: a formal of
/// type <c>T</c> that a call site closed over the lease is followed into the callee, and a field of type <c>T</c> that a store
/// closed over the lease is a store of the lease. A container holds a handle when its type arguments, its base types, the
/// interfaces it implements, its constraints or its anonymous members do.</para>
///
/// <para>The gate's own types are the only exception, because the gate is what mints and tracks the handles. The
/// request, closing and closed-lease handles are a separate, reviewed family: they may be kept only where a reviewed
/// census says so, and the work lease and the group may never be.</para>
/// </remarks>
internal sealed class AuthorityCarrierScanner
{
    internal const string Namespace = "RetroDownfall.Arcanum.Infrastructure.Data";

    internal const string GateType = Namespace + ".GrimoireConnectionAdmissionGate";

    /// <summary>The handles no code outside the gate may hold, with no census to excuse it.</summary>
    internal static readonly ImmutableArray<string> BannedAuthorities = ["IGrimoireWorkLease", "IGrimoireExternalEffectGroup"];

    /// <summary>The handles that may be held only where a reviewed census names the holder.</summary>
    internal static readonly ImmutableArray<string> ReviewedAuthorities = ["IGrimoireRequestLease", "IGrimoireClosingOwner", "IGrimoireExclusiveClosedLease"];

    private const int MaximumTypeDepth = 8;

    /// <summary>
    /// The framework callees that may be handed a handle because they only look at it: a null guard and a reference comparison.
    /// Every other callee that is declared outside the analysed source, or has no body to read, is a sink that keeps the handle.
    /// </summary>
    internal static readonly IReadOnlySet<string> ReviewedPassThroughs = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.ArgumentNullException.ThrowIfNull",
        "object.ReferenceEquals",
    };

    private readonly IReadOnlyList<Compilation> compilations;

    private readonly Dictionary<SyntaxTree, Compilation> owners = [];

    private readonly Dictionary<SyntaxTree, SemanticModel> models = [];

    private readonly Dictionary<ISymbol, string> carriers = new(SymbolEqualityComparer.Default);

    private readonly HashSet<AuthorityCarrier> findings = [];

    private readonly HashSet<SyntaxNode> queued = [];

    private readonly Queue<SyntaxNode> pending = new();

    private readonly Dictionary<ITypeSymbol, string?> holds = new(SymbolEqualityComparer.Default);

    // The functions (by their declaring syntax) that return a value derived from a carrier, and the roots that call them: a
    // root scanned before its callee learns the fact is scanned again when the fact arrives.
    private readonly Dictionary<SyntaxNode, string> returns = [];

    private readonly Dictionary<SyntaxNode, HashSet<SyntaxNode>> callers = [];

    // Every source method that overrides or implements a method, keyed by the syntax that declares the overridden or implemented one.
    private Dictionary<SyntaxNode, List<IMethodSymbol>>? overriders;

    internal AuthorityCarrierScanner(IReadOnlyList<Compilation> compilations)
    {
        this.compilations = compilations;

        foreach (Compilation compilation in compilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                owners[tree] = compilation;
            }
        }
    }

    /// <summary>Every carrier outside the gate, ordered, once each.</summary>
    internal IReadOnlyList<AuthorityCarrier> Scan()
    {
        foreach (Compilation compilation in compilations)
        {
            ScanDeclaredMembers(compilation.Assembly.GlobalNamespace);
        }

        foreach (Compilation compilation in compilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
                {
                    if (IsRoot(node))
                    {
                        Enqueue(node);
                    }
                }
            }
        }

        while (pending.Count > 0)
        {
            SyntaxNode root = pending.Dequeue();

            queued.Remove(root);

            ScanRoot(root);
        }

        return [.. findings.OrderBy(static finding => finding.ToString(), StringComparer.Ordinal)];
    }

    /// <summary>
    /// The authority a type can hold: the interface itself, anything that implements it, or a container of either.
    /// </summary>
    /// <remarks>
    /// A container holds a handle through its type arguments, its base types, the interfaces it implements, its type-parameter
    /// constraints, its tuple elements or its anonymous members. A delegate type holds nothing by its declared type; what a
    /// delegate holds is a closure, which is judged where it is made. A supertype such as <c>object</c> holds nothing by its
    /// declared type either; what it holds is whatever a handle was copied into it, which the flow judges.
    /// </remarks>
    internal static string? Holds(ITypeSymbol? type) => Holds(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), 0);

    private static string? Holds(ITypeSymbol? type, HashSet<ITypeSymbol> seen, int depth)
    {
        // A type already explored is either fruitless or on the way down, so it is not explored again: this ends every cycle
        // (a type that is its own type argument, a base type or an interface of itself).
        if (type is null or IErrorTypeSymbol || depth > MaximumTypeDepth || type.SpecialType != SpecialType.None || !seen.Add(type))
        {
            return null;
        }

        if (AuthorityNamed(type) is { } named)
        {
            return named;
        }

        switch (type)
        {
            case IArrayTypeSymbol array:
                return Holds(array.ElementType, seen, depth + 1);

            case ITypeParameterSymbol parameter:
                return parameter.ConstraintTypes.Select(constraint => Holds(constraint, seen, depth + 1)).FirstOrDefault(static name => name is not null);

            case INamedTypeSymbol { TypeKind: TypeKind.Delegate }:
                return null;

            case INamedTypeSymbol { IsTupleType: true } tuple:
                return tuple.TupleElements.Select(element => Holds(element.Type, seen, depth + 1)).FirstOrDefault(static name => name is not null);

            case INamedTypeSymbol { IsAnonymousType: true } anonymous:
                return anonymous.GetMembers().OfType<IPropertySymbol>().Select(property => Holds(property.Type, seen, depth + 1)).FirstOrDefault(static name => name is not null);

            case INamedTypeSymbol container:
                return ArgumentsHold(container, seen, depth)
                    ?? (container.BaseType is { } baseType ? Holds(baseType, seen, depth + 1) : null)
                    ?? container.AllInterfaces.Select(contract => ArgumentsHold(contract, seen, depth)).FirstOrDefault(static name => name is not null);

            default:
                return null;
        }
    }

    private static string? ArgumentsHold(INamedTypeSymbol type, HashSet<ITypeSymbol> seen, int depth) =>
        type.TypeArguments.Select(argument => Holds(argument, seen, depth + 1)).FirstOrDefault(static name => name is not null);

    private string? HoldsOf(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        if (!holds.TryGetValue(type, out string? held))
        {
            holds[type] = held = Holds(type);
        }

        return held;
    }

    private static string? AuthorityNamed(ITypeSymbol type)
    {
        if (IsAuthority(type))
        {
            return type.Name;
        }

        foreach (INamedTypeSymbol contract in type.AllInterfaces)
        {
            if (IsAuthority(contract))
            {
                return contract.Name;
            }
        }

        return null;
    }

    private static bool IsAuthority(ITypeSymbol type) => type is INamedTypeSymbol { TypeKind: TypeKind.Interface } named
        && named.ContainingNamespace.ToDisplayString() == Namespace
        && (BannedAuthorities.Contains(named.Name, StringComparer.Ordinal) || ReviewedAuthorities.Contains(named.Name, StringComparer.Ordinal));

    // A value of this type can refer to a handle at all: a number, a flag, a string or an enum cannot.
    private static bool CanHold(ITypeSymbol? type) => type is not null
        && type.TypeKind is not (TypeKind.Enum or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.Error)
        && type.SpecialType is not (SpecialType.System_Void or SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte
            or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal
            or SpecialType.System_String or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or SpecialType.System_DateTime);

    private static bool MentionsTypeParameter(ITypeSymbol? type, int depth = 0) => type switch
    {
        null => false,
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => MentionsTypeParameter(array.ElementType, depth + 1),
        INamedTypeSymbol named when depth < MaximumTypeDepth => named.TypeArguments.Any(argument => MentionsTypeParameter(argument, depth + 1)),
        _ => false,
    };

    private static bool InGate(ISymbol symbol)
    {
        for (INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.ToDisplayString() == GateType)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The namespace, containing types and member name of a symbol, with no generic or parameter noise.</summary>
    internal static string Qualified(ISymbol symbol)
    {
        List<string> parts = [];

        for (ISymbol? current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction })
            {
                continue;
            }

            parts.Add(current is IMethodSymbol { MethodKind: MethodKind.Constructor } ? ".ctor" : current.Name);
        }

        parts.Reverse();

        string name = string.Join('.', parts);

        string? space = symbol.ContainingNamespace?.ToDisplayString();

        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }

    private void ScanDeclaredMembers(INamespaceSymbol space)
    {
        foreach (ISymbol member in space.GetMembers())
        {
            if (member is INamespaceSymbol nested)
            {
                ScanDeclaredMembers(nested);
            }
            else if (member is INamedTypeSymbol type)
            {
                ScanDeclaredMembers(type);
            }
        }
    }

    private void ScanDeclaredMembers(INamedTypeSymbol type)
    {
        if (type.DeclaringSyntaxReferences.IsEmpty || InGate(type))
        {
            return;
        }

        foreach (ISymbol member in type.GetMembers())
        {
            switch (member)
            {
                case INamedTypeSymbol nested:
                    ScanDeclaredMembers(nested);

                    break;

                case IFieldSymbol { AssociatedSymbol: null } field when Holds(field.Type) is { } authority:
                    // Roslyn gives a captured primary-constructor parameter an implicit field named <parameter>P.
                    findings.Add(field.IsImplicitlyDeclared && field.Name.StartsWith('<') && field.Name.EndsWith(">P", StringComparison.Ordinal)
                        ? new("primary-constructor", Qualified(type) + "(" + field.Name[1..^2] + ")", authority)
                        : new("field", Qualified(field), authority));

                    break;

                case IPropertySymbol property when Holds(property.Type) is { } authority:
                    findings.Add(new("property", Qualified(property), authority));

                    break;

                case IMethodSymbol { Name: "Bind" } bind when bind.Parameters.Select(static parameter => Holds(parameter.Type)).FirstOrDefault(static name => name is not null) is { } authority:
                    findings.Add(new("bind", Qualified(bind), authority));

                    break;

                // A delegate-typed member named Bind is a binder in all but syntax: what it takes is what its delegate takes.
                case IPropertySymbol { Name: "Bind" } or IEventSymbol { Name: "Bind" } or IFieldSymbol { Name: "Bind", AssociatedSymbol: null }:
                    if (DelegateParametersHold(member is IPropertySymbol binder ? binder.Type : member is IEventSymbol bound ? bound.Type : ((IFieldSymbol)member).Type) is { } taken)
                    {
                        findings.Add(new("bind", Qualified(member), taken));
                    }

                    break;
            }
        }
    }

    private static string? DelegateParametersHold(ITypeSymbol type) => type is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod: { } invoke }
        ? invoke.Parameters.Select(static parameter => Holds(parameter.Type)).FirstOrDefault(static name => name is not null)
        : null;

    // The syntax a body is read from: a method, constructor, accessor or expression-bodied property, a field or property
    // initializer, a primary-constructor base argument list, or a top-level statement. Anything nested in one of them is read with it.
    private static bool IsRoot(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        AccessorDeclarationSyntax accessor => accessor.Body is not null || accessor.ExpressionBody is not null,
        ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax or IndexerDeclarationSyntax } => true,
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax } } } => true,
        EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax } => true,
        PrimaryConstructorBaseTypeSyntax => true,
        GlobalStatementSyntax => true,
        _ => false,
    };

    private void Enqueue(SyntaxNode root)
    {
        if (queued.Add(root))
        {
            pending.Enqueue(root);
        }
    }

    private SemanticModel Model(SyntaxTree tree)
    {
        if (!models.TryGetValue(tree, out SemanticModel? model))
        {
            models[tree] = model = owners[tree].GetSemanticModel(tree);
        }

        return model;
    }

    private static IOperation? RootOperation(SemanticModel model, SyntaxNode root) => root switch
    {
        ArrowExpressionClauseSyntax arrow => model.GetOperation(arrow.Expression),
        GlobalStatementSyntax global => model.GetOperation(global.Statement),
        _ => model.GetOperation(root),
    };

    private static ISymbol? EnclosingSymbol(SemanticModel model, SyntaxNode root) => root switch
    {
        BaseMethodDeclarationSyntax method => model.GetDeclaredSymbol(method),
        AccessorDeclarationSyntax accessor => model.GetDeclaredSymbol(accessor),
        ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax property } => model.GetDeclaredSymbol(property),
        ArrowExpressionClauseSyntax { Parent: IndexerDeclarationSyntax indexer } => model.GetDeclaredSymbol(indexer),
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } => model.GetDeclaredSymbol(variable),
        EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property } => model.GetDeclaredSymbol(property),
        PrimaryConstructorBaseTypeSyntax { Parent.Parent: TypeDeclarationSyntax type } => model.GetDeclaredSymbol(type),
        _ => null,
    };

    private void ScanRoot(SyntaxNode root)
    {
        SemanticModel model = Model(root.SyntaxTree);

        ISymbol? enclosing = EnclosingSymbol(model, root);

        if (enclosing is not null && InGate(enclosing))
        {
            return;
        }

        if (RootOperation(model, root) is not { } top)
        {
            return;
        }

        IOperation[] operations = [.. top.DescendantsAndSelf()];

        string holder = enclosing is null ? root.SyntaxTree.FilePath + ":" + root.SpanStart : Qualified(enclosing);

        (IReturnOperation Return, SyntaxNode Function)[] returned = ReturnsOf(top, root);

        // A local or formal holds a handle by its declared type, or because a handle was copied or passed into it. Marking only ever
        // adds a symbol, so it converges; the bound is a guard against a defect, never a limit the scan can reach by input.
        for (int round = 0, bound = operations.Length + 8; ; round++)
        {
            if (round > bound)
            {
                throw new InvalidOperationException("The carrier marking of " + holder + " did not converge.");
            }

            bool changed = false;

            foreach (IOperation operation in operations)
            {
                changed |= MarkFrom(operation);
            }

            foreach ((IReturnOperation ret, SyntaxNode function) in returned)
            {
                changed |= MarkReturn(ret, function);
            }

            if (!changed)
            {
                break;
            }
        }

        int closureOrdinal = 0;

        foreach (IOperation operation in operations)
        {
            switch (operation)
            {
                case ISimpleAssignmentOperation assignment:
                    Store(holder, assignment.Target, assignment.Value);

                    break;

                case ICoalesceAssignmentOperation assignment:
                    Store(holder, assignment.Target, assignment.Value);

                    break;

                case ICompoundAssignmentOperation assignment:
                    Store(holder, assignment.Target, assignment.Value);

                    break;

                case IDeconstructionAssignmentOperation assignment:
                    DeconstructionStore(holder, assignment);

                    break;

                case IFieldInitializerOperation initializer:
                    foreach (IFieldSymbol field in initializer.InitializedFields)
                    {
                        StoreInto(holder, field, field.OriginalDefinition.Type, initializer.Value);
                    }

                    break;

                case IPropertyInitializerOperation initializer:
                    foreach (IPropertySymbol property in initializer.InitializedProperties)
                    {
                        StoreInto(holder, property, property.OriginalDefinition.Type, initializer.Value);
                    }

                    break;

                case IAnonymousFunctionOperation or ILocalFunctionOperation:
                    Capture(holder, closureOrdinal++, operation);

                    break;

                case IMethodReferenceOperation { Instance: { } receiver, Parent: IDelegateCreationOperation }:
                    if (CarrierOf(receiver) is { } bound)
                    {
                        findings.Add(new("delegate-receiver", holder, bound));
                    }

                    break;

                case IInvocationOperation invocation:
                    Registration(holder, invocation);

                    Called(root, invocation.TargetMethod);

                    Arguments(holder, invocation.TargetMethod, invocation.Arguments);

                    break;

                case IObjectCreationOperation { Constructor: not null } creation:
                    Arguments(holder, creation.Constructor, creation.Arguments);

                    break;

                case IPropertyReferenceOperation { Arguments.Length: > 0 } indexer:
                    IndexerArguments(holder, indexer);

                    break;

                // A call bound at run time is a callee nobody can read.
                case IDynamicInvocationOperation dynamicCall:
                    DynamicArguments(holder, dynamicCall.Arguments);

                    break;

                case IDynamicObjectCreationOperation dynamicCreation:
                    DynamicArguments(holder, dynamicCreation.Arguments);

                    break;

                case IDynamicIndexerAccessOperation dynamicIndexer:
                    DynamicArguments(holder, dynamicIndexer.Arguments);

                    break;
            }
        }

        foreach ((ISymbol symbol, string authority) in carriers.Where(static pair => pair.Key is IParameterSymbol).ToArray())
        {
            IParameterSymbol parameter = (IParameterSymbol)symbol;

            // A parameter of a primary constructor is captured, and so kept, when the type has a field Roslyn made for it.
            if (parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
                && !constructor.ContainingType.DeclaringSyntaxReferences.IsEmpty
                && !InGate(constructor)
                && constructor.ContainingType.GetMembers("<" + parameter.Name + ">P").Length > 0)
            {
                findings.Add(new("primary-constructor", Qualified(constructor.ContainingType) + "(" + parameter.Name + ")", authority));
            }
        }
    }

    // The returns of the function the root reads and of the local functions inside it, each with the syntax of the function it returns
    // from. A lambda is a delegate's body, not a function a call can name, so what it returns is not tracked.
    private static (IReturnOperation Return, SyntaxNode Function)[] ReturnsOf(IOperation top, SyntaxNode root)
    {
        List<(IReturnOperation, SyntaxNode)> returned = [];

        Visit(top, root is MethodDeclarationSyntax ? root : null);

        return [.. returned];

        void Visit(IOperation operation, SyntaxNode? function)
        {
            switch (operation)
            {
                case IAnonymousFunctionOperation:
                    function = null;

                    break;

                case ILocalFunctionOperation local:
                    function = local.Symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

                    break;

                case IReturnOperation { ReturnedValue: not null } ret when function is not null:
                    returned.Add((ret, function));

                    break;
            }

            foreach (IOperation child in operation.ChildOperations)
            {
                Visit(child, function);
            }
        }
    }

    private bool MarkReturn(IReturnOperation ret, SyntaxNode function)
    {
        if (returns.ContainsKey(function) || ret.ReturnedValue is not { } value || CarrierOf(value) is not { } authority)
        {
            return false;
        }

        returns[function] = authority;

        // A caller scanned before this fact existed took the call's result for harmless.
        if (callers.TryGetValue(function, out HashSet<SyntaxNode>? roots))
        {
            foreach (SyntaxNode caller in roots)
            {
                Enqueue(caller);
            }
        }

        return true;
    }

    // The root calls a function whose return value may come to carry a handle later: it is scanned again when that is known.
    private void Called(SyntaxNode root, IMethodSymbol target)
    {
        if (DeclarationOf(target) is { } declaration && CanHold(target.ReturnType) && owners.ContainsKey(declaration.SyntaxTree))
        {
            if (!callers.TryGetValue(declaration, out HashSet<SyntaxNode>? roots))
            {
                callers[declaration] = roots = [];
            }

            roots.Add(root);
        }
    }

    private static SyntaxNode? DeclarationOf(IMethodSymbol method) =>
        method.OriginalDefinition.DeclaringSyntaxReferences is [{ } reference] ? reference.GetSyntax() : null;

    // One step of the marking of locals and formals that now hold a handle.
    private bool MarkFrom(IOperation operation)
    {
        switch (operation)
        {
            case IVariableDeclaratorOperation { Initializer: { } initializer } declarator:
                return Mark(declarator.Symbol, initializer.Value);

            case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local.IsRef: false } local } assignment:
                return Mark(local.Local, assignment.Value);

            case ISimpleAssignmentOperation { Target: IParameterReferenceOperation { Parameter.RefKind: RefKind.None } parameter } assignment:
                return Mark(parameter.Parameter, assignment.Value);

            case ICoalesceAssignmentOperation { Target: ILocalReferenceOperation { Local.IsRef: false } local } assignment:
                return Mark(local.Local, assignment.Value);

            // A handle written into an element or a type-parameter member of a local container is now held by that container.
            case ISimpleAssignmentOperation { Target: IArrayElementReferenceOperation { ArrayReference: { } array } } assignment when VariableOf(array) is { } variable:
                return Mark(variable, assignment.Value);

            case ISimpleAssignmentOperation { Target: IPropertyReferenceOperation { Instance: { } instance } property } assignment
                when ReadsAnElement(property.Property) && VariableOf(instance) is { } variable:
                return Mark(variable, assignment.Value);

            case ISimpleAssignmentOperation { Target: IFieldReferenceOperation { Instance: { } instance } field } assignment
                when ReadsAnElement(field.Field) && VariableOf(instance) is { } variable:
                return Mark(variable, assignment.Value);

            case IDeconstructionAssignmentOperation deconstruction:
            {
                bool changed = false;

                foreach ((IOperation target, IOperation value) in Pairs(deconstruction))
                {
                    foreach (ILocalSymbol local in DeclaredLocals(target))
                    {
                        changed |= Mark(local, value);
                    }
                }

                return changed;
            }

            case IForEachLoopOperation loop:
            {
                bool changed = false;

                foreach (ILocalSymbol local in DeclaredLocals(loop.LoopControlVariable))
                {
                    changed |= Mark(local, loop.Collection);
                }

                return changed;
            }

            case IIsPatternOperation isPattern:
                return MarkPattern(isPattern.Pattern, isPattern.Value);

            case ISwitchOperation switched:
                return switched.Cases.SelectMany(static switchCase => switchCase.Clauses).OfType<IPatternCaseClauseOperation>()
                    .Aggregate(false, (changed, clause) => MarkPattern(clause.Pattern, switched.Value) | changed);

            case ISwitchExpressionOperation switched:
                return switched.Arms.Aggregate(false, (changed, arm) => MarkPattern(arm.Pattern, switched.Value) | changed);

            case IInvocationOperation { Instance: ILocalReferenceOperation or IParameterReferenceOperation } invocation when AbsorbedBy(invocation):
                return MarkReceiverOf(invocation);

            case IInvocationOperation invocation when ReceiverOf(invocation) is { } receiver && CarrierOf(receiver) is not null:
            {
                // An out argument of a member that hands back an element of the receiver: TryGetValue(key, out var value).
                bool changed = false;

                foreach (IArgumentOperation argument in invocation.Arguments)
                {
                    if (argument.Parameter is { RefKind: RefKind.Out } parameter && MentionsTypeParameter(parameter.OriginalDefinition.Type))
                    {
                        foreach (ILocalSymbol local in DeclaredLocals(argument.Value))
                        {
                            changed |= Mark(local, receiver);
                        }
                    }
                }

                return changed;
            }

            default:
                return false;
        }
    }

    // A handle passed to a member the receiver declares in terms of a type parameter (Add, Enqueue, Push) is now held by the receiver.
    private bool AbsorbedBy(IInvocationOperation invocation) => invocation.Arguments.Any(argument =>
        argument.Parameter is { } parameter && MentionsTypeParameter(parameter.OriginalDefinition.Type) && CarrierOf(argument.Value) is not null);

    private bool MarkReceiverOf(IInvocationOperation invocation)
    {
        IOperation receiver = invocation.Instance!;

        ISymbol? symbol = receiver switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            _ => null,
        };

        IOperation? absorbed = invocation.Arguments.FirstOrDefault(argument =>
            argument.Parameter is { } parameter && MentionsTypeParameter(parameter.OriginalDefinition.Type) && CarrierOf(argument.Value) is not null)?.Value;

        return symbol is not null && absorbed is not null && Mark(symbol, absorbed);
    }

    private bool MarkPattern(IPatternOperation pattern, IOperation matched)
    {
        switch (pattern)
        {
            case IDeclarationPatternOperation { DeclaredSymbol: ILocalSymbol local }:
                return Mark(local, matched);

            case IRecursivePatternOperation recursive:
            {
                bool changed = recursive.DeclaredSymbol is ILocalSymbol local && Mark(local, matched);

                // A part of a handle holder is conservatively a holder of it.
                foreach (IPropertySubpatternOperation property in recursive.PropertySubpatterns)
                {
                    changed |= MarkPattern(property.Pattern, matched);
                }

                foreach (IPatternOperation part in recursive.DeconstructionSubpatterns)
                {
                    changed |= MarkPattern(part, matched);
                }

                return changed;
            }

            case IBinaryPatternOperation binary:
                return MarkPattern(binary.LeftPattern, matched) | MarkPattern(binary.RightPattern, matched);

            default:
                return false;
        }
    }

    private static ISymbol? VariableOf(IOperation operation) => operation switch
    {
        ILocalReferenceOperation { Local.IsRef: false } local => local.Local,
        IParameterReferenceOperation { Parameter.RefKind: RefKind.None } parameter => parameter.Parameter,
        _ => null,
    };

    // The locals a target declares or names: a declarator, a reference, a declaration expression, or a tuple of them.
    private static IEnumerable<ILocalSymbol> DeclaredLocals(IOperation? target) => target switch
    {
        IVariableDeclaratorOperation declarator => [declarator.Symbol],
        ILocalReferenceOperation { Local.IsRef: false } local => [local.Local],
        IDeclarationExpressionOperation declaration => DeclaredLocals(declaration.Expression),
        ITupleOperation tuple => tuple.Elements.SelectMany(DeclaredLocals),
        _ => [],
    };

    // The receiver whose elements a call hands back: the instance, or the first argument of an extension method called as a static.
    private static IOperation? ReceiverOf(IInvocationOperation invocation) =>
        invocation.Instance ?? (invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null);

    // The authority a value carries: the static type of an operand it is made of, or a copy, an element read or a call that extended the carriers.
    private string? CarrierOf(IOperation value)
    {
        foreach (IOperation leaf in Leaves(value))
        {
            if (HoldsOf(leaf.Type) is { } declared)
            {
                return declared;
            }

            string? derived = leaf switch
            {
                ILocalReferenceOperation local => carriers.GetValueOrDefault(local.Local),
                IParameterReferenceOperation parameter => carriers.GetValueOrDefault(parameter.Parameter),
                IArrayElementReferenceOperation element when CanHold(element.Type) => CarrierOf(element.ArrayReference),
                IPropertyReferenceOperation { Instance: { } instance } property when ReadsAnElement(property.Property) && CanHold(property.Type) => CarrierOf(instance),
                IFieldReferenceOperation { Instance: { } instance } field when ReadsAnElement(field.Field) && CanHold(field.Type) => CarrierOf(instance),
                IInvocationOperation invocation => CarrierOfCall(invocation),
                _ => null,
            };

            if (derived is not null)
            {
                return derived;
            }
        }

        return null;
    }

    // An indexer, a tuple or anonymous member, or a member typed by a type parameter hands back something the instance was made of.
    private static bool ReadsAnElement(ISymbol member) => member switch
    {
        IPropertySymbol { IsIndexer: true } => true,
        _ => member.ContainingType is { IsTupleType: true } or { IsAnonymousType: true }
            || member switch
            {
                IPropertySymbol property => property.OriginalDefinition.Type is ITypeParameterSymbol,
                IFieldSymbol field => field.OriginalDefinition.Type is ITypeParameterSymbol,
                _ => false,
            },
    };

    private string? CarrierOfCall(IInvocationOperation invocation)
    {
        // An authored function whose return value comes from a carrier hands one back.
        if (DeclarationOf(invocation.TargetMethod) is { } declaration && returns.TryGetValue(declaration, out string? returned) && CanHold(invocation.Type))
        {
            return returned;
        }

        // A member that returns something typed by a type parameter hands back an element of its receiver: Dequeue, First, GetValueOrDefault.
        return MentionsTypeParameter(invocation.TargetMethod.OriginalDefinition.ReturnType) && CanHold(invocation.Type) && ReceiverOf(invocation) is { } receiver
            ? CarrierOf(receiver)
            : null;
    }

    // The operands a value is made of without changing what it is: a conversion, a choice, a coalesce, a tuple, an await, an anonymous
    // object or an array literal still holds exactly the handles that went in.
    private static IEnumerable<IOperation> Leaves(IOperation value)
    {
        switch (value)
        {
            case IConversionOperation conversion:
                return Leaves(conversion.Operand);

            case IParenthesizedOperation parenthesized:
                return Leaves(parenthesized.Operand);

            case IAwaitOperation awaited:
                return Leaves(awaited.Operation);

            case IConditionalOperation conditional:
                return Leaves(conditional.WhenTrue).Concat(conditional.WhenFalse is null ? [] : Leaves(conditional.WhenFalse));

            case ICoalesceOperation coalesce:
                return Leaves(coalesce.Value).Concat(Leaves(coalesce.WhenNull));

            case ISwitchExpressionOperation switched:
                return switched.Arms.SelectMany(static arm => Leaves(arm.Value));

            case ITupleOperation tuple:
                return tuple.Elements.SelectMany(Leaves);

            case IAnonymousObjectCreationOperation anonymous:
                return anonymous.Initializers.SelectMany(static initializer => initializer is ISimpleAssignmentOperation assignment ? Leaves(assignment.Value) : Leaves(initializer));

            case IArrayCreationOperation { Initializer: { } initializer }:
                return initializer.ElementValues.SelectMany(Leaves);

            case ICollectionExpressionOperation collection:
                return collection.Elements.SelectMany(Leaves);

            case ISpreadOperation spread:
                return Leaves(spread.Operand);

            default:
                return [value];
        }
    }

    private bool Mark(ISymbol symbol, IOperation value)
    {
        ITypeSymbol? declared = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

        if (carriers.ContainsKey(symbol) || !CanHold(declared) || HoldsOf(declared) is not null || CarrierOf(value) is not { } authority)
        {
            return false;
        }

        carriers[symbol] = authority;

        return true;
    }

    private bool IsCarrierSymbol(ISymbol symbol, out string authority)
    {
        ITypeSymbol? declared = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

        if (HoldsOf(declared) is { } held)
        {
            authority = held;

            return true;
        }

        return carriers.TryGetValue(symbol, out authority!);
    }

    // The targets of a deconstruction, each with the value that reaches it: element by element when the right side is a tuple literal.
    private static IEnumerable<(IOperation Target, IOperation Value)> Pairs(IDeconstructionAssignmentOperation assignment)
    {
        IOperation[] targets = [.. Flatten(assignment.Target)];

        IOperation value = assignment.Value is IConversionOperation { Operand: ITupleOperation literal } ? literal : assignment.Value;

        if (value is ITupleOperation pairs && pairs.Elements.Length == targets.Length)
        {
            return targets.Zip(pairs.Elements, static (target, element) => (target, element));
        }

        return targets.Select(target => (target, assignment.Value));
    }

    private static IEnumerable<IOperation> Flatten(IOperation target) => target switch
    {
        ITupleOperation tuple => tuple.Elements.SelectMany(Flatten),
        IDeclarationExpressionOperation { Expression: ITupleOperation tuple } => tuple.Elements.SelectMany(Flatten),
        IDeclarationExpressionOperation declaration => [declaration.Expression],
        _ => [target],
    };

    private void DeconstructionStore(string holder, IDeconstructionAssignmentOperation assignment)
    {
        foreach ((IOperation target, IOperation value) in Pairs(assignment))
        {
            Store(holder, target, value);
        }
    }

    private void Store(string holder, IOperation target, IOperation value)
    {
        if (CarrierOf(value) is not { } authority)
        {
            return;
        }

        switch (target)
        {
            case IFieldReferenceOperation field:
                StoreInto(holder, field.Field, DeclaredType(field.Field), value);

                break;

            case IPropertyReferenceOperation property:
                StoreInto(holder, property.Property, DeclaredType(property.Property), value);

                break;

            case ILocalReferenceOperation { Local.IsRef: true }:
                findings.Add(new("ref-store", holder, authority));

                break;

            case IParameterReferenceOperation { Parameter: { RefKind: not RefKind.None } parameter }:
                if (HoldsOf(parameter.Type) is null)
                {
                    findings.Add(new("ref-store", holder, authority));
                }

                break;

            case IArrayElementReferenceOperation:
                findings.Add(new("store", holder + ".[]", authority));

                break;

            case IInvocationOperation:
            case IDynamicMemberReferenceOperation:
            case IDynamicIndexerAccessOperation:
                findings.Add(new("ref-store", holder, authority));

                break;
        }
    }

    // The type a member was written with. An authored generic member is judged by its declaration (a field of type T that a store closed
    // over the lease is a store into a T); a framework generic can only be judged by what it was closed over, which the container that
    // holds it already reveals (a Dictionary<string, IGrimoireWorkLease> field is a finding of its own).
    private static ITypeSymbol DeclaredType(ISymbol member)
    {
        ISymbol definition = member.DeclaringSyntaxReferences.IsEmpty ? member : member.OriginalDefinition;

        return definition switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => throw new InvalidOperationException("A store target is a field or a property."),
        };
    }

    // A store into a member whose own declared type holds the handle is already that member's finding; only a member that hides the
    // handle behind a supertype (object, IAsyncDisposable, a type parameter) is a finding of the store itself. The declared type is
    // the one the member was written with: a field of type T that a store closed over the lease is a store into a T.
    private void StoreInto(string holder, ISymbol member, ITypeSymbol declaredType, IOperation value)
    {
        if (InGate(member) || CarrierOf(value) is not { } authority || HoldsOf(declaredType) is not null)
        {
            return;
        }

        findings.Add(new("store", member.DeclaringSyntaxReferences.IsEmpty ? holder + "->" + Qualified(member) : Qualified(member), authority));
    }

    private void Capture(string holder, int ordinal, IOperation closure)
    {
        SyntaxNode syntax = closure.Syntax;

        foreach (IOperation operation in closure.Descendants())
        {
            if (operation is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance } instance)
            {
                // The closure keeps the object it runs on: a lease implementation that lends itself to a closure is kept by it.
                if (HoldsOf(instance.Type) is { } self)
                {
                    findings.Add(new("closure", holder + "{closure#" + ordinal + ":this}", self));
                }

                continue;
            }

            ISymbol? symbol = operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                _ => null,
            };

            if (symbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not { } declaration
                || (declaration.SyntaxTree == syntax.SyntaxTree && syntax.Span.Contains(declaration.Span)))
            {
                continue;
            }

            if (IsCarrierSymbol(symbol, out string authority))
            {
                findings.Add(new("closure", holder + "{closure#" + ordinal + ":" + symbol.Name + "}", authority));
            }
        }
    }

    // A handle registered as a service is kept by the container and resolvable from anywhere: the registration is the carrier.
    private void Registration(string holder, IInvocationOperation invocation)
    {
        if (!(invocation.TargetMethod.ContainingNamespace?.ToDisplayString() ?? string.Empty).StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal))
        {
            return;
        }

        string? authority = invocation.TargetMethod.TypeArguments.Select(argument => HoldsOf(argument)).FirstOrDefault(static name => name is not null)
            ?? invocation.Arguments.SelectMany(static argument => argument.Value.DescendantsAndSelf().OfType<ITypeOfOperation>())
                .Select(typeOf => HoldsOf(typeOf.TypeOperand)).FirstOrDefault(static name => name is not null);

        if (authority is not null)
        {
            findings.Add(new("service", holder, authority));
        }
    }

    private void DynamicArguments(string holder, ImmutableArray<IOperation> arguments)
    {
        foreach (IOperation argument in arguments)
        {
            if (CarrierOf(argument) is { } authority)
            {
                findings.Add(new("sink", holder + "->dynamic", authority));
            }
        }
    }

    // A handle used as an indexer's argument is lent to the indexer; an indexer is a pair of accessors the scan does not follow.
    private void IndexerArguments(string holder, IPropertyReferenceOperation indexer)
    {
        foreach (IArgumentOperation argument in indexer.Arguments)
        {
            if (argument.Parameter is not { } parameter || HoldsOf(parameter.Type) is not null || CarrierOf(argument.Value) is not { } authority)
            {
                continue;
            }

            IPropertySymbol property = indexer.Property.OriginalDefinition;

            findings.Add(new("sink", holder + "->" + property.ContainingType.ToDisplayString() + "." + property.Name, authority));
        }
    }

    private void Arguments(string holder, IMethodSymbol target, ImmutableArray<IArgumentOperation> arguments)
    {
        foreach (IArgumentOperation argument in arguments)
        {
            if (argument.Parameter is not { } parameter || CarrierOf(argument.Value) is not { } authority)
            {
                continue;
            }

            IMethodSymbol callee = target.OriginalDefinition;

            // A member named Bind that is handed a handle is a holder in the making, whatever the type it declares for it.
            if (callee.Name == "Bind")
            {
                findings.Add(new("bind", Qualified(callee), authority));

                continue;
            }

            IReadOnlyList<(IParameterSymbol Formal, IReadOnlyList<SyntaxNode> Bodies)> readable = Readable(parameter);

            // An authored formal is judged by its declaration: one that is declared as the handle is its own declaration's concern, and
            // one declared as a type parameter is followed even when this call closed it over the handle. A framework formal can only
            // be judged by the type it was closed over, which a container's own declaration already reveals.
            if (HoldsOf(readable.Count > 0 ? parameter.OriginalDefinition.Type : parameter.Type) is not null)
            {
                continue;
            }

            if (readable.Count > 0)
            {
                foreach ((IParameterSymbol formal, IReadOnlyList<SyntaxNode> bodies) in readable)
                {
                    // An authored callee that can be read: its formal now carries the handle, and its body is judged with it.
                    if (carriers.TryAdd(formal, authority))
                    {
                        foreach (SyntaxNode body in bodies)
                        {
                            Enqueue(body);
                        }
                    }
                }

                RecordProperty(parameter, authority);

                continue;
            }

            string identity = callee.ContainingType.ToDisplayString() + "." + callee.Name;

            if (!ReviewedPassThroughs.Contains(identity))
            {
                findings.Add(new("sink", holder + "->" + identity, authority));
            }
        }
    }

    // The formals a call can run its argument through: the callee's own, and those of every override and implementation of it that is
    // declared in the analysed source. Each is a source parameter of the compilation that owns it, which is what the body of the
    // callee refers to, however the call site came to see the callee (another compilation sees it through a retargeting layer).
    private IReadOnlyList<(IParameterSymbol Formal, IReadOnlyList<SyntaxNode> Bodies)> Readable(IParameterSymbol parameter)
    {
        List<(IParameterSymbol, IReadOnlyList<SyntaxNode>)> readable = [];

        IParameterSymbol definition = parameter.OriginalDefinition;

        if (definition.DeclaringSyntaxReferences is [{ } reference] && reference.GetSyntax() is ParameterSyntax syntax && owners.ContainsKey(syntax.SyntaxTree)
            && Model(syntax.SyntaxTree).GetDeclaredSymbol(syntax) is IParameterSymbol own)
        {
            SyntaxNode? declaration = syntax.Parent?.Parent;

            // A primary constructor is declared by its type: every member body and initializer of the type reads the formal. The
            // parameter list of anything else is read through the one body it belongs to.
            SyntaxNode[] bodies = declaration is TypeDeclarationSyntax
                ? [.. declaration.DescendantNodes().Where(IsRoot)]
                : declaration?.AncestorsAndSelf().FirstOrDefault(IsRoot) is { } enclosingRoot ? [enclosingRoot] : [];

            if (bodies.Length > 0 || declaration is TypeDeclarationSyntax)
            {
                readable.Add((own, bodies));
            }
        }

        if (definition.ContainingSymbol is IMethodSymbol method && DeclarationOf(method) is { } methodDeclaration)
        {
            foreach (IMethodSymbol overrider in OverridersOf(methodDeclaration))
            {
                if (overrider.DeclaringSyntaxReferences is [{ } overrideReference] && overrider.Parameters.Length > parameter.Ordinal
                    && overrideReference.GetSyntax().AncestorsAndSelf().FirstOrDefault(IsRoot) is { } body)
                {
                    readable.Add((overrider.Parameters[parameter.Ordinal], [body]));
                }
            }
        }

        return readable;
    }

    // Every source method that overrides or implements the method declared at the syntax, and the ones that do so in turn.
    private IEnumerable<IMethodSymbol> OverridersOf(SyntaxNode declaration)
    {
        overriders ??= IndexOverriders();

        HashSet<SyntaxNode> visited = [declaration];

        Queue<SyntaxNode> work = new([declaration]);

        while (work.Count > 0)
        {
            if (!overriders.TryGetValue(work.Dequeue(), out List<IMethodSymbol>? overriding))
            {
                continue;
            }

            foreach (IMethodSymbol overrider in overriding)
            {
                yield return overrider;

                if (DeclarationOf(overrider) is { } next && visited.Add(next))
                {
                    work.Enqueue(next);
                }
            }
        }
    }

    private Dictionary<SyntaxNode, List<IMethodSymbol>> IndexOverriders()
    {
        Dictionary<SyntaxNode, List<IMethodSymbol>> index = [];

        void Add(IMethodSymbol overridden, IMethodSymbol overrider)
        {
            if (DeclarationOf(overridden) is { } key && !overrider.DeclaringSyntaxReferences.IsEmpty && !InGate(overrider))
            {
                if (!index.TryGetValue(key, out List<IMethodSymbol>? list))
                {
                    index[key] = list = [];
                }

                if (!list.Contains(overrider, SymbolEqualityComparer.Default))
                {
                    list.Add(overrider);
                }
            }
        }

        void Visit(INamespaceOrTypeSymbol container)
        {
            foreach (ISymbol member in container.GetMembers())
            {
                switch (member)
                {
                    case INamespaceSymbol space:
                        Visit(space);

                        break;

                    case INamedTypeSymbol type:
                        if (!type.DeclaringSyntaxReferences.IsEmpty)
                        {
                            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>().Where(static method => method.OverriddenMethod is not null))
                            {
                                Add(method.OverriddenMethod!, method);
                            }

                            foreach (INamedTypeSymbol contract in type.AllInterfaces)
                            {
                                foreach (IMethodSymbol contractMember in contract.GetMembers().OfType<IMethodSymbol>())
                                {
                                    if (type.TypeKind != TypeKind.Interface && type.FindImplementationForInterfaceMember(contractMember) is IMethodSymbol implementation)
                                    {
                                        Add(contractMember, implementation);
                                    }
                                }
                            }
                        }

                        Visit(type);

                        break;
                }
            }
        }

        foreach (Compilation compilation in compilations)
        {
            Visit(compilation.Assembly.GlobalNamespace);
        }

        return index;
    }

    // A record's positional parameter is also an auto-property the constructor stores into: handing it a handle is a store, whether
    // or not the type has any other body to read.
    private void RecordProperty(IParameterSymbol parameter, string authority)
    {
        if (parameter.OriginalDefinition.DeclaringSyntaxReferences is [{ } reference] && reference.GetSyntax() is ParameterSyntax { Parent.Parent: RecordDeclarationSyntax record } syntax
            && owners.ContainsKey(syntax.SyntaxTree)
            && Model(syntax.SyntaxTree).GetDeclaredSymbol(record) is INamedTypeSymbol type
            && type.GetMembers(syntax.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault() is { } property
            && HoldsOf(property.Type) is null)
        {
            findings.Add(new("store", Qualified(property), authority));
        }
    }
}
