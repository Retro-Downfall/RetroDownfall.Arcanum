using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // These finite framework members only construct or store metadata. Their
        // arguments, initializers and authored bodies remain in ordinary traversal.
        private bool IsReviewedHttpMetadataMethod(IMethodSymbol method, SyntaxNode? node, AuthoredMember? context)
        {
            if (method.AssociatedSymbol is IPropertySymbol property
                && method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet)
            {
                return IsReviewedHttpMetadataProperty(property, method.MethodKind == MethodKind.PropertySet, node, context);
            }

            if (method is not { MethodKind: MethodKind.Constructor, IsStatic: false, IsVirtual: false, IsAbstract: false, Arity: 0 }) return false;

            if (IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName()))
            {
                // HttpMessageInvoker's constructor logging may call virtual GetHashCode
                // on both client and handler. A base initializer or an opaque handler
                // therefore cannot borrow this exact new-client construction proof.
                return !httpConnectCertificate && context is not null
                    && HasExactParameterTypes(method, "System.Net.Http.HttpMessageHandler", "System.Boolean")
                    && node is BaseObjectCreationExpressionSyntax creation
                    && HasClosedNonOwningClientOrigin(context, creation,
                        new CleanupProvenanceContext { CollectionIndexerProof = true });
            }

            if (IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpRequestMessage", typeof(HttpRequestMessage).Assembly.GetName()))
            {
                return HasExactParameterTypes(method, "System.Net.Http.HttpMethod", "System.String")
                    && HasClosedA2AUriParserUniverse();
            }

            return IsExactFrameworkType(method.ContainingType, "System.Net.Http.Headers.AuthenticationHeaderValue", typeof(System.Net.Http.Headers.AuthenticationHeaderValue).Assembly.GetName())
                    && HasExactParameterTypes(method, "System.String", "System.String")
                || method.ContainingType.IsSealed
                    && IsExactFrameworkType(method.ContainingType, "System.Net.Http.SocketsHttpHandler", typeof(SocketsHttpHandler).Assembly.GetName())
                    && method.Parameters.IsEmpty;
        }

        private bool IsReviewedHttpMetadataProperty(IPropertySymbol property, bool mutation, SyntaxNode? node, AuthoredMember? context)
        {
            if (property.IsIndexer || !property.Parameters.IsEmpty || property.ReturnsByRef || property.ReturnsByRefReadonly
                || (mutation ? property.SetMethod : property.GetMethod) is not { IsVirtual: false, IsAbstract: false, Arity: 0 } accessor
                || accessor.IsStatic != property.IsStatic) return false;

            if (mutation)
            {
                if (!accessor.ReturnsVoid || accessor.Parameters is not [{ RefKind: RefKind.None } value]
                    || !SymbolEqualityComparer.Default.Equals(value.Type, property.Type)) return false;
            }
            else if (!accessor.Parameters.IsEmpty || !SymbolEqualityComparer.Default.Equals(accessor.ReturnType, property.Type)) return false;

            if (!mutation && property.IsStatic && property.Name == "Get")
            {
                return IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpMethod", typeof(HttpMethod).Assembly.GetName())
                    && IsExactFrameworkType(property.Type, "System.Net.Http.HttpMethod", typeof(HttpMethod).Assembly.GetName());
            }

            if (property.IsStatic) return false;

            if (mutation && property.Name == "Timeout"
                && IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName()))
            {
                return IsExactFrameworkType(property.Type, "System.TimeSpan", typeof(TimeSpan).Assembly.GetName());
            }

            if (!mutation && property.Name == "Headers"
                && IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpRequestMessage", typeof(HttpRequestMessage).Assembly.GetName()))
            {
                return IsExactFrameworkType(property.Type, "System.Net.Http.Headers.HttpRequestHeaders", typeof(System.Net.Http.Headers.HttpRequestHeaders).Assembly.GetName());
            }

            if (mutation && property.Name == "Authorization"
                && IsExactFrameworkType(property.ContainingType, "System.Net.Http.Headers.HttpRequestHeaders", typeof(System.Net.Http.Headers.HttpRequestHeaders).Assembly.GetName()))
            {
                return IsExactFrameworkType(property.Type, "System.Net.Http.Headers.AuthenticationHeaderValue", typeof(System.Net.Http.Headers.AuthenticationHeaderValue).Assembly.GetName());
            }

            if (!mutation || !IsExactFrameworkType(property.ContainingType, "System.Net.Http.SocketsHttpHandler", typeof(SocketsHttpHandler).Assembly.GetName())) return false;

            if (property.Name is "AllowAutoRedirect" or "UseProxy") return property.Type.SpecialType == SpecialType.System_Boolean;

            // Storing a delegate is not evidence that its future execution completes.
            // Reuse the existing exact factory/callback certificate only outside that
            // certificate's nested graph; it never grants network or cleanup authority.
            return property.Name == "ConnectCallback" && !httpConnectCertificate && context is not null
                && IsExactHttpConnectCallbackType(property.Type)
                && node is ExpressionSyntax { Parent: AssignmentExpressionSyntax assignment } target
                && assignment.Left == target
                && assignment.Parent is InitializerExpressionSyntax { Parent: BaseObjectCreationExpressionSyntax }
                && HasClosedHttpCleanupHandlerFactory(context);
        }

        private static bool IsExactHttpConnectCallbackType(ITypeSymbol type) =>
            type is INamedTypeSymbol
            {
                TypeArguments: [ITypeSymbol connection, ITypeSymbol cancellation, INamedTypeSymbol { TypeArguments: [ITypeSymbol stream] } result],
            } callback
            && IsExactFrameworkType(callback, "System.Func`3", typeof(Func<,,>).Assembly.GetName())
            && IsExactFrameworkType(connection, "System.Net.Http.SocketsHttpConnectionContext", typeof(SocketsHttpConnectionContext).Assembly.GetName())
            && IsExactFrameworkType(cancellation, "System.Threading.CancellationToken", typeof(CancellationToken).Assembly.GetName())
            && IsExactFrameworkType(result, "System.Threading.Tasks.ValueTask`1", typeof(ValueTask<>).Assembly.GetName())
            && IsExactFrameworkType(stream, "System.IO.Stream", typeof(Stream).Assembly.GetName());
    }
}
