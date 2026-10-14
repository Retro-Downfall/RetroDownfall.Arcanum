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
/// bound to them or a heap location written from them. The scan is semantic: it follows a handle through locals, through
/// supertypes (<c>object</c>, <c>IAsyncDisposable</c>, a type parameter) and through the authored callees it is handed to,
/// and it refuses a handle given to a callee it cannot read.
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

    private const int FixpointRounds = 8;

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
    /// A delegate type holds nothing by its declared type; what a delegate holds is a closure, which is judged where it is
    /// made. A supertype such as <c>object</c> holds nothing by its declared type either; what it holds is whatever a
    /// handle was copied into it, which the flow judges.
    /// </remarks>
    internal static string? Holds(ITypeSymbol? type, int depth = 0)
    {
        if (type is null or IErrorTypeSymbol || depth > 6)
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
                return Holds(array.ElementType, depth + 1);

            case INamedTypeSymbol { TypeKind: TypeKind.Delegate }:
                return null;

            case INamedTypeSymbol { IsTupleType: true } tuple:
                return tuple.TupleElements.Select(element => Holds(element.Type, depth + 1)).FirstOrDefault(static name => name is not null);

            case INamedTypeSymbol generic:
                return generic.TypeArguments.Select(argument => Holds(argument, depth + 1)).FirstOrDefault(static name => name is not null);

            default:
                return null;
        }
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
            }
        }
    }

    // The syntax a body is read from: a method, constructor, accessor or expression-bodied property, a field or property
    // initializer, or a top-level statement. Anything nested in one of them is read with it.
    private static bool IsRoot(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax method => method.Body is not null || method.ExpressionBody is not null,
        AccessorDeclarationSyntax accessor => accessor.Body is not null || accessor.ExpressionBody is not null,
        ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax or IndexerDeclarationSyntax } => true,
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax } } } => true,
        EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax } => true,
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

        // A local or formal holds a handle by its declared type, or because a handle was copied or passed into it.
        for (int round = 0; round < FixpointRounds; round++)
        {
            bool changed = false;

            foreach (IOperation operation in operations)
            {
                switch (operation)
                {
                    case IVariableDeclaratorOperation { Initializer: { } initializer } declarator:
                        changed |= Mark(declarator.Symbol, initializer.Value);

                        break;

                    case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local.IsRef: false } local } assignment:
                        changed |= Mark(local.Local, assignment.Value);

                        break;

                    case ISimpleAssignmentOperation { Target: IParameterReferenceOperation { Parameter.RefKind: RefKind.None } parameter } assignment:
                        changed |= Mark(parameter.Parameter, assignment.Value);

                        break;

                    case ICoalesceAssignmentOperation { Target: ILocalReferenceOperation { Local.IsRef: false } local } assignment:
                        changed |= Mark(local.Local, assignment.Value);

                        break;
                }
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
                        StoreInto(holder, field, field.Type, initializer.Value);
                    }

                    break;

                case IPropertyInitializerOperation initializer:
                    foreach (IPropertySymbol property in initializer.InitializedProperties)
                    {
                        StoreInto(holder, property, property.Type, initializer.Value);
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

                    Arguments(holder, invocation.TargetMethod, invocation.Arguments);

                    break;

                case IObjectCreationOperation { Constructor: not null } creation:
                    Arguments(holder, creation.Constructor, creation.Arguments);

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

    // The authority a value carries: the static type of an operand it is made of, or a copy or call that extended the carriers.
    private string? CarrierOf(IOperation value)
    {
        foreach (IOperation leaf in Leaves(value))
        {
            if (Holds(leaf.Type) is { } declared)
            {
                return declared;
            }

            ISymbol? symbol = leaf switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                _ => null,
            };

            if (symbol is not null && carriers.TryGetValue(symbol, out string? marked))
            {
                return marked;
            }
        }

        return null;
    }

    // The operands a value is made of without changing what it is: a conversion, a choice, a coalesce, a tuple or an array
    // literal still holds exactly the handles that went in.
    private static IEnumerable<IOperation> Leaves(IOperation value)
    {
        switch (value)
        {
            case IConversionOperation conversion:
                return Leaves(conversion.Operand);

            case IParenthesizedOperation parenthesized:
                return Leaves(parenthesized.Operand);

            case IConditionalOperation conditional:
                return Leaves(conditional.WhenTrue).Concat(conditional.WhenFalse is null ? [] : Leaves(conditional.WhenFalse));

            case ICoalesceOperation coalesce:
                return Leaves(coalesce.Value).Concat(Leaves(coalesce.WhenNull));

            case ISwitchExpressionOperation switched:
                return switched.Arms.SelectMany(static arm => Leaves(arm.Value));

            case ITupleOperation tuple:
                return tuple.Elements.SelectMany(Leaves);

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

        if (carriers.ContainsKey(symbol) || Holds(declared) is not null || CarrierOf(value) is not { } authority)
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

        if (Holds(declared) is { } held)
        {
            authority = held;

            return true;
        }

        return carriers.TryGetValue(symbol, out authority!);
    }

    private void DeconstructionStore(string holder, IDeconstructionAssignmentOperation assignment)
    {
        IOperation[] targets = assignment.Target is ITupleOperation tuple ? [.. tuple.Elements] : [assignment.Target];

        IOperation value = assignment.Value is IConversionOperation { Operand: ITupleOperation literal } ? literal : assignment.Value;

        if (value is ITupleOperation pairs && pairs.Elements.Length == targets.Length)
        {
            for (int index = 0; index < targets.Length; index++)
            {
                Store(holder, targets[index], pairs.Elements[index]);
            }

            return;
        }

        foreach (IOperation target in targets)
        {
            Store(holder, target, assignment.Value);
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
                StoreInto(holder, field.Field, field.Field.Type, value);

                break;

            case IPropertyReferenceOperation property:
                StoreInto(holder, property.Property, property.Property.Type, value);

                break;

            case ILocalReferenceOperation { Local.IsRef: true }:
                findings.Add(new("ref-store", holder, authority));

                break;

            case IParameterReferenceOperation { Parameter: { RefKind: not RefKind.None } parameter }:
                if (Holds(parameter.Type) is null)
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

    // A store into a member whose own type holds the handle is already that member's finding; only a member that hides the
    // handle behind a supertype (object, IAsyncDisposable, a type parameter) is a finding of the store itself.
    private void StoreInto(string holder, ISymbol member, ITypeSymbol memberType, IOperation value)
    {
        if (InGate(member) || CarrierOf(value) is not { } authority || Holds(memberType) is not null)
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

        string? authority = invocation.TargetMethod.TypeArguments.Select(static argument => Holds(argument)).FirstOrDefault(static name => name is not null)
            ?? invocation.Arguments.SelectMany(static argument => argument.Value.DescendantsAndSelf().OfType<ITypeOfOperation>())
                .Select(static typeOf => Holds(typeOf.TypeOperand)).FirstOrDefault(static name => name is not null);

        if (authority is not null)
        {
            findings.Add(new("service", holder, authority));
        }
    }

    private void Arguments(string holder, IMethodSymbol target, ImmutableArray<IArgumentOperation> arguments)
    {
        foreach (IArgumentOperation argument in arguments)
        {
            if (argument.Parameter is not { } parameter || Holds(parameter.Type) is not null || CarrierOf(argument.Value) is not { } authority)
            {
                continue;
            }

            IMethodSymbol callee = target.OriginalDefinition;

            SyntaxNode? declaration = callee.DeclaringSyntaxReferences.Length == 1 ? callee.DeclaringSyntaxReferences[0].GetSyntax() : null;

            if (declaration is not null && owners.ContainsKey(declaration.SyntaxTree))
            {
                // A primary constructor is declared by its type: every member body and initializer of the type reads the formal.
                SyntaxNode[] bodies = declaration is TypeDeclarationSyntax
                    ? [.. declaration.DescendantNodes().Where(IsRoot)]
                    : declaration.AncestorsAndSelf().FirstOrDefault(IsRoot) is { } enclosingRoot ? [enclosingRoot] : [];

                if (bodies.Length > 0)
                {
                    // An authored callee that can be read: its formal now carries the handle, and its body is judged with it.
                    if (carriers.TryAdd(parameter.OriginalDefinition, authority))
                    {
                        foreach (SyntaxNode body in bodies)
                        {
                            Enqueue(body);
                        }
                    }

                    continue;
                }
            }

            string identity = callee.ContainingType.ToDisplayString() + "." + callee.Name;

            if (!ReviewedPassThroughs.Contains(identity))
            {
                findings.Add(new("sink", holder + "->" + identity, authority));
            }
        }
    }
}
