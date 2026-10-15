using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

// The one matcher for "who calls this method". The Admitted Entry Census and the Lease-Handoff chain pin each pin the exact set of callers
// of a door, and a door is the same thing to both: a method that other methods are related to. The matcher is keyed on the method's
// identity, never on a name or on the type a caller happens to name, so a pin on an interface slot is not bypassed by a caller of the
// class registered for it:
//   - the method itself (the link named by its containing type, an interface slot or a class);
//   - the interface slots it implements and the methods that implement a slot, explicit implementations included, and the method a class
//     inherits from its base class to implement a slot the derived class names (the base declares the method and does not implement the slot);
//   - a slot of a generic interface that an implementer closes in its own base list, matched on the open definition;
//   - another interface member that the same implementation satisfies (an alias interface);
//   - the methods those override and the methods that override them;
//   - the calls through any of them, through the concrete type, inside a lambda or a local function, and every method group that names one.
// A name inside `nameof` is not a reference, and a type declared in a nested position is found like any other. Methods are identified by
// their documentation identity, which is one string however many compilations see the type, so a symbol seen through another compilation's
// retargeting layer is still the same method.
internal static partial class HostedGrimoireProducerInventory
{
    // One reference to a link: the name, the model that binds it, and the link method it binds to (the exact symbol, or one of the
    // candidates when overload resolution did not choose).
    internal sealed record MethodReference(Compilation Compilation, SemanticModel Model, SimpleNameSyntax Name, IMethodSymbol Target)
    {
        // The call this name invokes, however it is reached (x.Name(...), Name(...), x?.Name(...)), or null when the name is a method group, a
        // delegate conversion or any other reference that is not a call.
        internal InvocationExpressionSyntax? Call => Name.Parent switch
        {
            InvocationExpressionSyntax call when call.Expression == Name => call,
            MemberAccessExpressionSyntax access when access.Name == Name && access.Parent is InvocationExpressionSyntax call && call.Expression == access => call,
            MemberBindingExpressionSyntax binding when binding.Name == Name && binding.Parent is InvocationExpressionSyntax call && call.Expression == binding => call,
            _ => null,
        };

        internal bool Invoked => Call is not null;
    }

    // Every type the compilations declare, nested types included: a second implementation of a door may be private to some other class.
    internal static IEnumerable<INamedTypeSymbol> DeclaredTypesOf(IReadOnlyList<Compilation> compilations) => compilations
        .SelectMany(static compilation => DeclaredTypesIn(compilation.Assembly.GlobalNamespace));

    private static IEnumerable<INamedTypeSymbol> DeclaredTypesIn(INamespaceSymbol scope)
    {
        foreach (INamedTypeSymbol type in scope.GetTypeMembers())
        {
            foreach (INamedTypeSymbol nested in DeclaredTypeAndNested(type))
            {
                yield return nested;
            }
        }

        foreach (INamespaceSymbol child in scope.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol type in DeclaredTypesIn(child))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> DeclaredTypeAndNested(INamedTypeSymbol type)
    {
        yield return type;

        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            foreach (INamedTypeSymbol inner in DeclaredTypeAndNested(nested))
            {
                yield return inner;
            }
        }
    }

    // The declared types that have the contract among their interfaces, directly, through a base class or through an interface that
    // extends it: every implementation of a door is a finding for a pin, wherever it is declared.
    internal static IReadOnlyList<INamedTypeSymbol> ImplementersOf(IReadOnlyList<Compilation> compilations, string contractType) =>
        [.. DeclaredTypesOf(compilations).Where(type => type.AllInterfaces.Any(candidate => TypeKey(candidate.OriginalDefinition) == contractType))];

    // True when a call to either method may run the other: one is a slot the other implements, or one overrides the other.
    private static bool MethodsRelated(IMethodSymbol first, IMethodSymbol second)
    {
        static bool Implements(IMethodSymbol slot, IMethodSymbol implementation) =>
            slot.ContainingType.TypeKind == TypeKind.Interface
            && implementation.ContainingType.TypeKind != TypeKind.Interface
            && implementation.ContainingType.FindImplementationForInterfaceMember(slot) is IMethodSymbol found
            && MethodKey(found) == MethodKey(implementation);

        static bool Overrides(IMethodSymbol derived, IMethodSymbol inherited)
        {
            for (IMethodSymbol? overridden = derived.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
            {
                if (MethodKey(overridden) == MethodKey(inherited))
                {
                    return true;
                }
            }

            return false;
        }

        return Implements(first, second) || Implements(second, first) || Overrides(first, second) || Overrides(second, first);
    }

    // The interface slots of the given name that each declared class or struct runs a method for, as pairs of the slot's identity and the
    // implementing method's identity. The type's own view of the interface decides: it resolves a method the type inherits from its base
    // class, and a slot of a generic interface that the type closes, which the implementing class's own members and the open slot never show.
    private static IReadOnlyList<(string Slot, string Implementation)> SlotImplementations(IReadOnlyList<Compilation> compilations, string method)
    {
        HashSet<(string, string)> pairs = [];

        foreach (INamedTypeSymbol type in DeclaredTypesOf(compilations).Where(static type => type.TypeKind is TypeKind.Class or TypeKind.Struct))
        {
            foreach (INamedTypeSymbol contract in type.AllInterfaces)
            {
                foreach (IMethodSymbol slot in contract.GetMembers(method).OfType<IMethodSymbol>())
                {
                    if (type.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation)
                    {
                        pairs.Add((MethodKey(slot), MethodKey(implementation)));
                    }
                }
            }
        }

        return [.. pairs];
    }

    // The link methods of a door: the method `method` of the type `containingType`, and every method of that name that is related to a link
    // already found, to a fixed point. Two methods are related when one is a slot the other implements (for any class or struct that names the
    // slot, the implementation being the method that type runs, declared by it or inherited), or one overrides the other.
    private static IReadOnlyList<IMethodSymbol> MethodLinks(IReadOnlyList<Compilation> compilations, string containingType, string method, out IReadOnlyList<IMethodSymbol> pool)
    {
        IMethodSymbol[] named = [.. DeclaredTypesOf(compilations)
            .SelectMany(type => type.GetMembers().OfType<IMethodSymbol>().Where(candidate => candidate.Name == method
                || candidate.ExplicitInterfaceImplementations.Any(implemented => implemented.Name == method)))];

        pool = named;

        // Only methods the compilations declare are links: a slot of a framework interface is not one, so an implementation of it does not draw
        // every other implementation of the framework's slot into the door.
        HashSet<string> declared = [.. named.Select(MethodKey)];

        IReadOnlyList<(string Slot, string Implementation)> implementations = [.. SlotImplementations(compilations, method)
            .Where(pair => declared.Contains(pair.Slot) && declared.Contains(pair.Implementation))];

        HashSet<string> identities = [.. named.Where(candidate => TypeKey(candidate.ContainingType) == containingType).Select(MethodKey)];

        for (bool grew = true; grew;)
        {
            grew = false;

            IMethodSymbol[] known = [.. named.Where(candidate => identities.Contains(MethodKey(candidate)))];

            foreach (IMethodSymbol candidate in named.Where(candidate => !identities.Contains(MethodKey(candidate))))
            {
                if (known.Any(link => MethodsRelated(candidate, link)))
                {
                    grew |= identities.Add(MethodKey(candidate));
                }
            }

            foreach ((string slot, string implementation) in implementations)
            {
                if (identities.Contains(slot) != identities.Contains(implementation))
                {
                    identities.Add(identities.Contains(slot) ? implementation : slot);

                    grew = true;
                }
            }
        }

        return [.. named.Where(candidate => identities.Contains(MethodKey(candidate)))];
    }

    // Every reference to a link of the door in the compilations, in compilation, tree and source order. A name that binds to no link is not
    // a reference, a name inside nameof is not one either, and a reference that is not an invocation is reported like one that is; the
    // caller reads `Invoked` to tell a call from a method group or a delegate that could be invoked somewhere it cannot see.
    internal static IReadOnlyList<MethodReference> MethodReferencesTo(IReadOnlyList<Compilation> compilations, string containingType, string method)
    {
        IReadOnlyList<IMethodSymbol> links = MethodLinks(compilations, containingType, method, out _);

        // A pin on a door that is not declared would match nothing and pass for ever.
        if (!links.Any(link => TypeKey(link.ContainingType) == containingType))
        {
            throw new InvalidOperationException($"No method '{method}' of '{containingType}' is declared in the compilations, so a pin on it would match nothing.");
        }

        HashSet<string> identities = [.. links.Select(MethodKey)];

        List<MethodReference> found = [];

        foreach (Compilation compilation in compilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                SemanticModel model = compilation.GetSemanticModel(tree);

                foreach (SimpleNameSyntax name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>().Where(candidate => candidate.Identifier.ValueText == method))
                {
                    if (InNameof(model, name))
                    {
                        continue;
                    }

                    SymbolInfo info = model.GetSymbolInfo(name);

                    IEnumerable<IMethodSymbol> referenced = info.Symbol is IMethodSymbol exact ? [exact] : info.CandidateSymbols.OfType<IMethodSymbol>();

                    IMethodSymbol? target = referenced.FirstOrDefault(reference => identities.Contains(MethodKey(reference)) || links.Any(link => MethodsRelated(reference, link)));

                    if (target is not null)
                    {
                        found.Add(new(compilation, model, name, target));
                    }
                }
            }
        }

        return found;
    }

    private static bool InNameof(SemanticModel model, SimpleNameSyntax name) => name.Ancestors().OfType<InvocationExpressionSyntax>().Any(call =>
        call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && model.GetConstantValue(call).HasValue);
}
