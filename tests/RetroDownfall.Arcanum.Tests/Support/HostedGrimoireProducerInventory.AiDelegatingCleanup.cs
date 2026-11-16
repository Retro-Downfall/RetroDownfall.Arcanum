using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // The pinned constructors store their one receiver; they do not execute its virtual members.
        // Argument expressions and every authored constructor body still traverse normally.
        private static bool IsExactAiDelegatingConstructor(IMethodSymbol method) =>
            method.MethodKind == MethodKind.Constructor
            && method.Parameters is [{ RefKind: RefKind.None } inner]
            && (IsExactFrameworkType(method.ContainingType, "Microsoft.Extensions.AI.DelegatingChatClient",
                    typeof(Microsoft.Extensions.AI.DelegatingChatClient).Assembly.GetName())
                && IsExactFrameworkType(inner.Type, "Microsoft.Extensions.AI.IChatClient",
                    typeof(Microsoft.Extensions.AI.IChatClient).Assembly.GetName())
                || IsExactFrameworkType(method.ContainingType, "Microsoft.Extensions.AI.DelegatingEmbeddingGenerator`2",
                    typeof(Microsoft.Extensions.AI.DelegatingEmbeddingGenerator<,>).Assembly.GetName())
                && IsExactEmbeddingGeneratorContract(inner.Type));

        private bool TryResolveClosedAiDelegatingExternalCleanup(AuthoredMember member, ExpressionSyntax resource,
            IMethodSymbol cleanup)
        {
            if (cleanup.Name != "Dispose" || cleanup.IsStatic || cleanup.Parameters.Length != 0
                || cleanup.ReturnType.SpecialType != SpecialType.System_Void
                || !(IsExactFrameworkType(cleanup.ContainingType, "Microsoft.Extensions.AI.DelegatingChatClient",
                        typeof(Microsoft.Extensions.AI.DelegatingChatClient).Assembly.GetName())
                    || IsExactFrameworkType(cleanup.ContainingType, "Microsoft.Extensions.AI.DelegatingEmbeddingGenerator`2",
                        typeof(Microsoft.Extensions.AI.DelegatingEmbeddingGenerator<,>).Assembly.GetName()))) return false;

            return HasClosedAiAdapterCleanupValue(member, resource, new CleanupProvenanceContext(), new(StringComparer.Ordinal));
        }

        private bool HasClosedAiAdapterCleanupValue(AuthoredMember member, ExpressionSyntax resource,
            CleanupProvenanceContext proof, HashSet<string> path)
        {
            if (proof.RemainingWork-- <= 0) return false;

            resource = StripTransparentExpression(resource);

            if (resource is BinaryExpressionSyntax tryCast && tryCast.IsKind(SyntaxKind.AsExpression)
                && member.Model.GetOperation(tryCast) is IConversionOperation { IsTryCast: true, OperatorMethod: null })
            {
                // This cleanup receiver is either the same instance or null. The built-in as
                // conversion executes no authored conversion and does not change its inner owner.
                resource = tryCast.Left;
            }

            string key = MemberIdentity(member) + "/" + Location(resource);

            if (!path.Add(key)) return false;

            try
            {
                CleanupValueFlow sources = CleanupValueFlowOf(member, resource, proof);

                if (!sources.Complete || sources.Values.Count == 0) return false;

                foreach (BoundValueSource source in sources.Values)
                {
                    ExpressionSyntax value = StripTransparentExpression(source.Expression);

                    if (value is InvocationExpressionSyntax factory
                        && source.Caller.Model.GetSymbolInfo(factory).Symbol is IMethodSymbol adapter
                        && (IsExactChatSdkAdapterFactory(adapter) || IsExactEmbeddingSdkAdapterFactory(adapter))) continue;

                    if (value is not BaseObjectCreationExpressionSyntax creation
                        || source.Caller.Model.GetOperation(creation) is not IObjectCreationOperation
                        {
                            Constructor: { } constructor,
                            Type: INamedTypeSymbol { IsSealed: true } type,
                        }
                        || type.GetMembers().OfType<IMethodSymbol>().Any(static method => method.Name == "Dispose"
                            || method.ExplicitInterfaceImplementations.Any(static slot => slot.Name == "Dispose")))
                    {
                        return false;
                    }

                    // Generic method resolution intentionally has no body for a primary constructor.
                    // Reuse the constructor context lane that binds its declared parameters instead.
                    if (ResolveConstructorContextTarget(constructor, source.Caller.Model.Compilation) is not { } target) return false;

                    SyntaxNode? initializer = target.Syntax switch
                    {
                        ConstructorDeclarationSyntax declaration => declaration.Initializer,
                        TypeDeclarationSyntax declaration => declaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().SingleOrDefault(),
                        ParameterListSyntax { Parent: TypeDeclarationSyntax declaration } => declaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().SingleOrDefault(),
                        _ => null,
                    };

                    ArgumentListSyntax? arguments = initializer switch
                    {
                        ConstructorInitializerSyntax ordinary => ordinary.ArgumentList,
                        PrimaryConstructorBaseTypeSyntax primary => primary.ArgumentList,
                        _ => null,
                    };

                    if (arguments is null || arguments.Arguments.Count != 1
                        || target.Model.GetSymbolInfo(initializer!).Symbol is not IMethodSymbol parent
                        || !IsExactAiDelegatingConstructor(parent)) return false;

                    ArgumentSyntax inner = arguments.Arguments[0];

                    AuthoredMember bound = BindCleanupConstructorArguments(source.Caller, creation, target);

                    if (!HasClosedAiAdapterCleanupValue(bound, inner.Expression, proof, path)) return false;
                }

                // Every complete value source is a pinned ret-only SDK adapter, optionally wrapped
                // by sealed authored classes inheriting the exact delegator cleanup unchanged.
                return true;
            }
            finally
            {
                path.Remove(key);
            }
        }
    }
}
