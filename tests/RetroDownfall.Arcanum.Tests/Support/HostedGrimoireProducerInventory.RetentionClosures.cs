using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    internal static string? ProbeExactInheritedRegisteredSlot(
        IReadOnlyList<CSharpCompilation> compilations, string enclosingType, string memberName, string invokedName,
        string? constructionOwner = null, string? constructionMember = null) =>
        new ProducerGraph(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
            DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false, sitesOnly: true)
            .ProbeExactInheritedRegisteredSlot(enclosingType, memberName, invokedName, constructionOwner, constructionMember);

    private sealed partial class ProducerGraph
    {
        internal string? ProbeExactInheritedRegisteredSlot(string enclosingType, string memberName, string invokedName,
            string? constructionOwner, string? constructionMember)
        {
            AuthoredMember member = AuthoredMembers.Single(candidate => TypeKey(candidate.Symbol.ContainingType) == enclosingType
                && candidate.Symbol.Name == memberName);

            if (constructionOwner is not null)
            {
                AuthoredMember caller = AuthoredMembers.Single(candidate => TypeKey(candidate.Symbol.ContainingType) == constructionOwner
                    && candidate.Symbol.Name == constructionMember);

                BaseObjectCreationExpressionSyntax creation = caller.Syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
                    .Single(candidate => caller.Model.GetTypeInfo(candidate).Type is { } type && TypeKey(type) == enclosingType);

                if (caller.Model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor
                    || ResolveConstructorContextTarget(constructor, caller.Model.Compilation) is not { } context) return null;

                member = MergeValueContext(member, BindConstructorArguments(caller, creation, context));
            }

            InvocationExpressionSyntax call = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(candidate => member.Model.GetSymbolInfo(candidate).Symbol is IMethodSymbol method
                    && method.Name == invokedName && method.ContainingType.TypeKind == TypeKind.Interface);

            IMethodSymbol invoked = (IMethodSymbol)member.Model.GetSymbolInfo(call).Symbol!;

            AuthoredMember? target = ResolveExactInheritedRegisteredSlot(invoked, member, call);

            return target is null ? null : Normalize(target.Symbol);
        }

        // A derived contract can inherit a public slot without registering that public contract.
        // Resolve only this proven receiver, never a global interface alias. Stopped-host callers
        // can also forward a constructed guard instead of obtaining it from a container.
        private AuthoredMember? ResolveExactInheritedRegisteredSlot(
            IMethodSymbol method,
            AuthoredMember member,
            SyntaxNode node)
        {
            if (method.ContainingType.TypeKind != TypeKind.Interface
                || node is not InvocationExpressionSyntax call
                || AdmissionReceiver(member, call) is not { } receiver
                || member.Model.GetTypeInfo(receiver).Type is not INamedTypeSymbol { TypeKind: TypeKind.Interface } contract
                || SameBoundType(contract, member.Model.Compilation, method.ContainingType, member.Model.Compilation)
                || !contract.AllInterfaces.Any(inherited => SameBoundType(inherited, member.Model.Compilation,
                    method.ContainingType, member.Model.Compilation)))
            {
                return null;
            }

            if (!IsExactDiBoundCleanupReceiver(member, receiver, contract, []))
            {
                if (member.Model.GetSymbolInfo(receiver).Symbol is not IParameterSymbol
                    {
                        ContainingSymbol: IMethodSymbol { MethodKind: MethodKind.Constructor },
                    } captured
                    || !ConstructorParameterNeverWritten(captured))
                {
                    return null;
                }

                CleanupProvenanceContext proof = new();

                // The whole owning type must never write or alias the captured constructor slot.
                // Provenance then follows its exact bound value and rejects opaque or incomplete sources.
                CleanupProvenance provenance = CleanupProvenanceOf(member, receiver, proof);

                return proof.RemainingWork > 0
                    && provenance is
                    {
                        Complete: true,
                        HasAuthoredSource: true,
                        Candidates: [CleanupTypeCandidate { Type: INamedTypeSymbol { IsSealed: true } exact }],
                    }
                        ? ResolveExactInheritedImplementation(method, member, exact)
                        : null;
            }

            BoundTypeIdentity identity = TypeIdentity(contract, member.Model.Compilation);

            if (incompleteInterfaceBindings.Contains(identity)) return null;

            ServiceBinding[] registrations = bindings.Where(binding => binding.Contract == identity).ToArray();

            if (registrations is not [ServiceBinding registration]
                || !boundImplementationTypes.TryGetValue(registration.Implementation, out INamedTypeSymbol? concrete)
                || !concrete.IsSealed)
            {
                return null;
            }

            return ResolveExactInheritedImplementation(method, member, concrete);
        }

        private AuthoredMember? ResolveExactInheritedImplementation(
            IMethodSymbol method,
            AuthoredMember member,
            INamedTypeSymbol concrete)
        {
            IMethodSymbol[] implementations = concrete.AllInterfaces
                .Where(inherited => SameBoundType(inherited, member.Model.Compilation,
                    method.ContainingType, member.Model.Compilation))
                .SelectMany(static inherited => inherited.GetMembers())
                .OfType<IMethodSymbol>()
                .Where(slot => MethodKey(slot) == MethodKey(method))
                .Select(concrete.FindImplementationForInterfaceMember)
                .OfType<IMethodSymbol>()
                .ToArray();

            return implementations is [IMethodSymbol implementation]
                ? Resolve(implementation, member.Model.Compilation)
                : null;
        }

        // Encoding.UTF8 is the runtime's immutable builtin replacement-fallback singleton.
        // GetEncoder creates a fresh encoder; the caller's existing local-use proof rejects any
        // mutation, escape or custom fallback. Preserve the old canonical UTF8 replacement behavior.
        // https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Text/UTF8Encoding.cs
        private static bool IsExactBuiltinUtf8EncoderFactory(ExpressionSyntax expression, SemanticModel model) =>
            model.GetOperation(expression) is IInvocationOperation
            {
                TargetMethod: { Name: "GetEncoder", IsStatic: false, Parameters.Length: 0 } factory,
                Instance: IPropertyReferenceOperation
                {
                    Instance: null,
                    Property: { Name: "UTF8", IsStatic: true, Parameters.Length: 0 } property,
                },
            }
            && IsExactFrameworkType(factory.ContainingType, "System.Text.Encoding", typeof(System.Text.Encoding).Assembly.GetName())
            && IsExactFrameworkType(factory.ReturnType, "System.Text.Encoder", typeof(System.Text.Encoder).Assembly.GetName())
            && IsExactFrameworkType(property.ContainingType, "System.Text.Encoding", typeof(System.Text.Encoding).Assembly.GetName())
            && SymbolEqualityComparer.Default.Equals(property.Type, factory.ContainingType);

        // This is still an eager enumeration terminal: iterator bodies, enumeration ownership and
        // cleanup retain their normal traversal. The predicate overload is deliberately excluded.
        private static bool IsExactLongCountTerminal(IMethodSymbol method) =>
            method is { IsStatic: true, Name: "LongCount", Arity: 1, ReturnType.SpecialType: SpecialType.System_Int64 }
            && IsExactFrameworkType(method.ContainingType, "System.Linq.Enumerable", typeof(Enumerable).Assembly.GetName())
            && method.Parameters is [IParameterSymbol
            {
                RefKind: RefKind.None,
                Type: INamedTypeSymbol { TypeArguments: [ITypeSymbol element] } source,
            }]
            && IsExactFrameworkType(source, "System.Collections.Generic.IEnumerable`1", typeof(IEnumerable<>).Assembly.GetName())
            && SymbolEqualityComparer.Default.Equals(element, method.TypeArguments[0]);
    }
}
