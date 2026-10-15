using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>One call to the container's registration surface that names a watched type as a generic argument or a typeof operand.</summary>
/// <param name="Model">The model that binds the call.</param>
/// <param name="Node">The call, or the construction of a service descriptor.</param>
/// <param name="Method">The registration method or descriptor constructor.</param>
/// <param name="Watched">The watched type the call names.</param>
/// <param name="Enclosing">The member that holds the call, a closure collapsed into the member that wrote it.</param>
/// <param name="Location">The tree path and line of the call.</param>
internal sealed record ContainerRegistration(SemanticModel Model, SyntaxNode Node, IMethodSymbol Method, ITypeSymbol Watched, string Enclosing, string Location)
{
    /// <summary>The lifetime the call registers, from its name or from the lifetime it is given. A pooled context is a singleton pool.</summary>
    internal string Lifetime => Method.Name switch
    {
        string name when name.Contains("Singleton", StringComparison.Ordinal) => "Singleton",
        string name when name.Contains("Scoped", StringComparison.Ordinal) => "Scoped",
        string name when name.Contains("Transient", StringComparison.Ordinal) => "Transient",
        string name when name.Contains("Pool", StringComparison.Ordinal) => "Pool",
        string name when name.Contains("Factory", StringComparison.Ordinal) => "Factory",
        string name when name.StartsWith("AddDbContext", StringComparison.Ordinal) => GivenLifetime ?? "Scoped",
        _ => GivenLifetime ?? "Unknown",
    };

    // A ServiceLifetime constant passed to the call, when the call takes one.
    private string? GivenLifetime => (Node as InvocationExpressionSyntax)?.ArgumentList.Arguments
        .Select(argument => Model.GetSymbolInfo(argument.Expression).Symbol)
        .OfType<IFieldSymbol>()
        .FirstOrDefault(static constant => constant.ContainingType.Name == "ServiceLifetime")?.Name;
}

/// <summary>
/// The one reader of the container's registration surface. Everything in the dependency-injection namespaces counts except resolving a
/// service from a provider, which is consumption and never a registration. A registration names a type as a generic argument (including
/// the service type an overload infers from its factory) or as a typeof operand of the call.
/// </summary>
internal static class ContainerRegistrationScanner
{
    internal static IReadOnlyList<ContainerRegistration> Find(IReadOnlyList<Compilation> compilations, Func<ITypeSymbol, bool> watched, Func<SyntaxTree, bool>? includeTree = null)
    {
        List<ContainerRegistration> registrations = [];

        foreach (Compilation compilation in compilations)
        {
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                if (includeTree is not null && !includeTree(tree))
                {
                    continue;
                }

                SemanticModel model = compilation.GetSemanticModel(tree);

                foreach (SyntaxNode node in tree.GetRoot().DescendantNodes().Where(static node => node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax))
                {
                    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol method || !IsContainerRegistrationApi(method))
                    {
                        continue;
                    }

                    ITypeSymbol[] named = [.. method.TypeArguments, .. TypeofOperands(model, node)];

                    foreach (ITypeSymbol type in named.Where(watched))
                    {
                        registrations.Add(new(
                            model,
                            node,
                            method,
                            type,
                            AuthorityCarrierScanner.Qualified(EnclosingMember(model.GetEnclosingSymbol(node.SpanStart))),
                            tree.FilePath + ":" + (tree.GetLineSpan(node.Span).StartLinePosition.Line + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
            }
        }

        return registrations;
    }

    // The container's registration surface: everything in the dependency-injection namespaces except resolving a service from a provider.
    internal static bool IsContainerRegistrationApi(IMethodSymbol method)
    {
        string space = method.ContainingNamespace?.ToDisplayString() ?? "";

        return space.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)
            && method.ContainingType.Name is not ("ServiceProviderServiceExtensions" or "ServiceProviderKeyedServiceExtensions" or "ActivatorUtilities");
    }

    private static IEnumerable<ITypeSymbol> TypeofOperands(SemanticModel model, SyntaxNode node) =>
        node.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>()
            .Select(typeOf => model.GetTypeInfo(typeOf.Type).Type)
            .OfType<ITypeSymbol>();

    /// <summary>The member a closure sits in: a lambda or local function is part of the member that wrote it.</summary>
    internal static ISymbol EnclosingMember(ISymbol? enclosing)
    {
        ISymbol? current = enclosing;

        while (current is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            current = current.ContainingSymbol;
        }

        return current ?? throw new InvalidOperationException("A reference has no enclosing member.");
    }
}
