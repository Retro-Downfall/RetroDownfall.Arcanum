using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed partial class ProducerGraph
    {
        // Cleanup only: no constructor/send/callback body is removed from traversal.
        private bool TryResolveClosedHttpConstructionCleanup(AuthoredMember member, ExpressionSyntax resource,
            ITypeSymbol staticType, string name, out ExternalCleanupResolution? external)
        {
            external = null;

            string type = TypeKey(staticType);

            if (name != "Dispose" || !IsExactFrameworkType(staticType, type, typeof(HttpClient).Assembly.GetName())) return false;

            bool closed = type switch
            {
                "System.Net.Http.HttpMessageHandler" => HasClosedSocketsHandlerOrigin(member, resource, new CleanupProvenanceContext { CollectionIndexerProof = true }),
                "System.Net.Http.HttpClient" => HasClosedNonOwningClientOrigin(member, resource, new CleanupProvenanceContext { CollectionIndexerProof = true })
                    || HasClosedNamedHttpClientOrigin(member, resource),
                "System.Net.Http.ByteArrayContent" => HasClosedByteArrayContentOrigin(member, resource),
                "System.Net.Http.HttpRequestMessage" => HasClosedSpendRequestOwner(member, resource),
                _ => false,
            };

            if (!closed) return false;

            external = new(type + ".Dispose", HostedProducerSiteKind.ProviderCall);

            return true;
        }

        private bool HasClosedHttpHandlerDispatch(AuthoredMember member, ExpressionSyntax resource,
            ITypeSymbol staticType, IReadOnlyList<CleanupTypeCandidate> candidates) => candidates.Count != 0
            && IsExactFrameworkType(staticType, "System.Net.Http.HttpMessageHandler", typeof(HttpMessageHandler).Assembly.GetName())
            && candidates.All(candidate => IsExactFrameworkType(candidate.Type, "System.Net.Http.SocketsHttpHandler", typeof(SocketsHttpHandler).Assembly.GetName()))
            && HasClosedSocketsHandlerOrigin(member, resource, new CleanupProvenanceContext { CollectionIndexerProof = true });

        // Exact allocation candidates use the external lane rather than the factory
        // fallback. Authenticate the selected inherited framework slot, then reuse the
        // same current resource origin and closed-use proof.
        private bool TryResolveClosedHttpConstructionExternalCleanup(AuthoredMember member, ExpressionSyntax resource,
            ITypeSymbol exactType, IMethodSymbol cleanup, out ExternalCleanupResolution? external)
        {
            external = null;

            if (cleanup is not { Name: "Dispose", IsStatic: false, Arity: 0, Parameters.Length: 0, ReturnsVoid: true }) return false;

            string type = TypeKey(exactType);

            if (!IsExactFrameworkType(exactType, type, typeof(HttpClient).Assembly.GetName())) return false;

            string? slot = type switch
            {
                "System.Net.Http.HttpRequestMessage" => "System.Net.Http.HttpRequestMessage",
                "System.Net.Http.HttpClient" => "System.Net.Http.HttpMessageInvoker",
                "System.Net.Http.SocketsHttpHandler" => "System.Net.Http.HttpMessageHandler",
                "System.Net.Http.ByteArrayContent" => "System.Net.Http.HttpContent",
                _ => null,
            };

            return slot is not null
                && IsExactFrameworkType(cleanup.ContainingType, slot, typeof(HttpClient).Assembly.GetName())
                && member.Model.GetTypeInfo(resource).Type is { } staticType
                && TryResolveClosedHttpConstructionCleanup(member, resource, staticType, cleanup.Name, out external);
        }

        // An omitted optional transport may make this conditional invocation unreachable.
        // Require complete EMPTY value flow in this exact caller context; the mere existence
        // of a default DI registration or a nullable Func declaration proves nothing here.
        private bool IsExactAbsentConditionalHttpHandlerFactory(AuthoredMember member,
            ConditionalAccessExpressionSyntax expression, CleanupProvenanceContext proof)
        {
            if (member.Model.GetOperation(expression) is not IConditionalAccessOperation
                { Operation.Syntax: ExpressionSyntax receiver,
                    WhenNotNull: IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke } invocation }
                || invocation.Arguments.Length != 0
                || member.Model.GetTypeInfo(receiver).Type is not INamedTypeSymbol
                { TypeArguments: [ITypeSymbol handler], DelegateInvokeMethod.Parameters.Length: 0 } factory
                || !IsExactFrameworkType(factory, "System.Func`1", typeof(Func<>).Assembly.GetName())
                || !IsExactFrameworkType(handler, "System.Net.Http.HttpMessageHandler", typeof(HttpMessageHandler).Assembly.GetName())) return false;

            CleanupValueFlow absent = CleanupValueFlowOf(member, receiver, proof);

            return absent.Complete && absent.Values.Count == 0;
        }

        private readonly Dictionary<(GraphMemberIdentity Member, string Environment), bool> closedHttpCleanupHandlerFactories = [];

        private bool HasClosedHttpCleanupHandlerFactory(AuthoredMember factory)
        {
            // A connect certificate cannot recursively certify a handler factory,
            // including one reached through the existing cleanup-origin proof.
            if (httpConnectCertificate) return false;

            EvaluationEnvironmentFingerprint environment = EvaluationEnvironmentIdentity(factory);

            if (!environment.Complete || environment.ContainsBackreference) return false;

            var key = (MemberIdentity(factory), environment.Identity);

            if (!closedHttpCleanupHandlerFactories.TryGetValue(key, out bool closed))
            {
                closed = HasExactOwnedSocketsHandlerFactory(factory, []);

                closedHttpCleanupHandlerFactories.Add(key, closed);
            }

            return closed;
        }

        private bool HasClosedSocketsHandlerOrigin(AuthoredMember member, ExpressionSyntax expression, CleanupProvenanceContext proof)
        {
            if (!HasClosedHttpConstructionUses(member, expression, handler: true)) return false;

            CleanupValueFlow flow = CleanupValueFlowOf(member, expression, proof);

            return flow.Complete && flow.Values.Count != 0 && flow.Values.All(value =>
                value.Caller.Model.GetOperation(value.Expression) is IObjectCreationOperation
                { Constructor: { Parameters.Length: 0 } constructor }
                && IsExactFrameworkType(constructor.ContainingType, "System.Net.Http.SocketsHttpHandler", typeof(SocketsHttpHandler).Assembly.GetName())
                && HasClosedHttpCleanupHandlerFactory(value.Caller));
        }

        private bool HasClosedNonOwningClientOrigin(AuthoredMember member, ExpressionSyntax expression, CleanupProvenanceContext proof)
        {
            if (!HasClosedHttpConstructionUses(member, expression, handler: false)) return false;

            CleanupValueFlow flow = CleanupValueFlowOf(member, expression, proof);

            return flow.Complete && flow.Values.Count != 0 && flow.Values.All(value =>
                value.Expression is BaseObjectCreationExpressionSyntax creation
                && HasClosedHttpConstructionUses(value.Caller, creation, handler: false)
                && IsExactNonOwningHttpClientCreation(value.Caller, creation, out ExpressionSyntax? handler)
                && handler is not null && HasClosedSocketsHandlerOrigin(value.Caller, handler, proof));
        }

        private bool HasClosedNamedHttpClientOrigin(AuthoredMember member, ExpressionSyntax expression)
        {
            CleanupValueFlow flow = CleanupValueFlowOf(member, expression, new CleanupProvenanceContext
            {
                CollectionIndexerProof = true,
                ClosedNamedHttpClient = true,
            });

            // The exact default factory constructs the HttpClient and retains the handler.
            // This proves its disposal target, while disposal still requires an Effect group.
            return flow.Complete && flow.Values.Count != 0 && flow.Values.All(value =>
                value.Expression is InvocationExpressionSyntax factory
                && value.Caller.Model.GetSymbolInfo(factory).Symbol is IMethodSymbol method
                && IsReviewedNamedHttpFactoryCreation(method, factory, value.Caller));
        }

        private static bool IsExactNamedHttpClientBufferAssignment(AuthoredMember member, AssignmentExpressionSyntax assignment, ISymbol storage) =>
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && member.Model.GetOperation(assignment.Left) is IPropertyReferenceOperation
            {
                Instance: ILocalReferenceOperation { Local: { } receiver },
                Property: { Name: "MaxResponseContentBufferSize" } property,
            }
            && SymbolEqualityComparer.Default.Equals(storage, receiver)
            && IsReviewedA2AHttpStorageProperty(property, mutation: true);

        private bool HasClosedByteArrayContentOrigin(AuthoredMember member, ExpressionSyntax expression)
        {
            CleanupValueFlow flow = CleanupValueFlowOf(member, expression, new CleanupProvenanceContext { CollectionIndexerProof = true });

            return flow.Complete && flow.Values.Count != 0 && flow.Values.All(value =>
                value.Caller.Model.GetOperation(value.Expression) is IObjectCreationOperation { Constructor: { } constructor }
                && IsExactByteArrayContentConstruction(constructor));
        }

        private static bool IsExactNonOwningHttpClientCreation(AuthoredMember member, BaseObjectCreationExpressionSyntax creation,
            out ExpressionSyntax? handler)
        {
            handler = null;

            if (member.Model.GetOperation(creation) is not IObjectCreationOperation
                { Constructor: { } constructor, Arguments: [IArgumentOperation first, IArgumentOperation second] } allocated
                || !IsExactFrameworkType(constructor.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName())
                || !HasExactParameterTypes(constructor, "System.Net.Http.HttpMessageHandler", "System.Boolean")
                || first.Parameter is not { Ordinal: 0, RefKind: RefKind.None }
                || second.Parameter is not { Ordinal: 1, RefKind: RefKind.None }
                || first.InConversion.MethodSymbol is not null || first.OutConversion.MethodSymbol is not null
                || second.InConversion.MethodSymbol is not null || second.OutConversion.MethodSymbol is not null
                || second.Value.ConstantValue is not { HasValue: true, Value: false }
                || first.Value.Syntax is not ExpressionSyntax source) return false;

            if (allocated.Initializer is { } initializer
                && (initializer.Initializers is not [ISimpleAssignmentOperation
                    { Target: IPropertyReferenceOperation { Property: { Name: "Timeout" } property }, Value: IFieldReferenceOperation { Field: { Name: "InfiniteTimeSpan", IsStatic: true } timeout } }]
                    || !IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName())
                    || !IsExactFrameworkType(timeout.ContainingType, "System.Threading.Timeout", typeof(Timeout).Assembly.GetName()))) return false;

            handler = source;

            return true;
        }

        private static ISymbol? HttpConstructionStorage(AuthoredMember member, ExpressionSyntax expression)
        {
            ISymbol? storage = member.Model.GetSymbolInfo(expression).Symbol;

            if (storage is ILocalSymbol or IParameterSymbol) return storage;

            return expression.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } declaration
                ? member.Model.GetDeclaredSymbol(declaration) as ILocalSymbol
                : null;
        }

        // Initial scope is a directly owned local/formal with only the production uses.
        // Aliasing/escape/replacement does not acquire this construction proof.
        private bool HasClosedHttpConstructionUses(AuthoredMember member, ExpressionSyntax expression, bool handler)
        {
            expression = StripTransparentExpression(expression);

            ISymbol? storage = HttpConstructionStorage(member, expression);

            if (storage is not ILocalSymbol
                && (handler || storage is not IParameterSymbol { RefKind: RefKind.None } || !HasExactSpendSendBody(member))
                || storage is null || HasCollectionValueReplacement(member, storage)) return false;

            foreach (IdentifierNameSyntax reference in member.Model.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(reference => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(reference).Symbol, storage)))
            {
                ExpressionSyntax use = reference;

                while (use.Parent is ParenthesizedExpressionSyntax parentheses) use = parentheses;

                if (use.Parent is UsingStatementSyntax { Expression: { } resource } && resource == use) continue;

                if (use.Parent is ArgumentSyntax { RefKindKeyword.RawKind: 0 } argument
                    && argument.Parent?.Parent is BaseObjectCreationExpressionSyntax creation
                    && handler && IsExactNonOwningHttpClientCreation(member, creation, out ExpressionSyntax? supplied)
                    && supplied is not null && StripTransparentExpression(supplied).Span == use.Span) continue;

                if (!handler && use.Parent is ArgumentSyntax { RefKindKeyword.RawKind: 0 } forwarded
                    && forwarded.Parent?.Parent is InvocationExpressionSyntax call
                    && member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    && Resolve(method, member.Model.Compilation) is { } target
                    && HasExactSpendSendBody(target)
                    && AdmissionArguments(member, call).Any(value => value.Parameter?.Ordinal == 0
                        && value.RefKind == RefKind.None && StripTransparentExpression(value.Expression).Span == use.Span)) continue;

                // This is useful inside the bound forwarding owner; all its uses are
                // additionally closed by the exact owner body/strong-symbol certificate.
                if (!handler && use.Parent is MemberAccessExpressionSyntax { Expression: { } receiver, Parent: InvocationExpressionSyntax send }
                    && receiver == use && member.Model.GetSymbolInfo(send).Symbol is IMethodSymbol network
                    && IsExactHttpResponseFactory(network)) continue;

                return false;
            }

            return true;
        }

        private const string ExactSpendSendBody = """
            {
                using HttpRequestMessage request = new(HttpMethod.Get,
                    "https://openrouter.ai/api/v1/generation?id=" + Uri.EscapeDataString(GenerationId));

                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _secret);

                return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            """;

        // A narrow owner certificate, not a generic request/handler exemption. Closed
        // caller uses alone cannot prevent an authored handler installing request.Content.
        private bool HasExactSpendSendBody(AuthoredMember member)
        {
            if (member.Symbol is not { Name: "SendAsync", MethodKind: MethodKind.Ordinary, IsStatic: false, IsVirtual: false, IsAsync: true, Arity: 0,
                    Parameters: [IParameterSymbol { RefKind: RefKind.None } client, IParameterSymbol { RefKind: RefKind.None } ct] }
                || member.Symbol.ContainingAssembly.Name != "RetroDownfall.Arcanum.Infrastructure"
                || TypeKey(member.Symbol.ContainingType) != "RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendSourceCredential.VerifiedSource"
                || !member.Symbol.ContainingType.IsSealed
                || !HasStockAsyncPublication(member)
                || !IsExactFrameworkType(client.Type, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName())
                || client.Name != "client" || ct.Name != "ct"
                || !IsExactCancellationToken(ct.Type)
                || member.Symbol.ReturnType is not INamedTypeSymbol { TypeArguments: [ITypeSymbol response] } task
                || !IsExactFrameworkType(task, "System.Threading.Tasks.Task`1", typeof(Task<>).Assembly.GetName())
                || !IsExactFrameworkType(response, "System.Net.Http.HttpResponseMessage", typeof(HttpResponseMessage).Assembly.GetName())
                || member.Syntax is not MethodDeclarationSyntax { Body: { } body }
                || !SyntaxFactory.AreEquivalent(body, SyntaxFactory.ParseStatement(ExactSpendSendBody))) return false;

            BaseObjectCreationExpressionSyntax[] creations = body.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>().ToArray();

            if (creations.Length != 2 || member.Model.GetSymbolInfo(creations[0]).Symbol is not IMethodSymbol request
                || !IsExactFrameworkType(request.ContainingType, "System.Net.Http.HttpRequestMessage", typeof(HttpRequestMessage).Assembly.GetName())
                || !HasExactParameterTypes(request, "System.Net.Http.HttpMethod", "System.String")
                || member.Model.GetSymbolInfo(creations[1]).Symbol is not IMethodSymbol authorization
                || !IsExactFrameworkType(authorization.ContainingType, "System.Net.Http.Headers.AuthenticationHeaderValue", typeof(System.Net.Http.Headers.AuthenticationHeaderValue).Assembly.GetName())
                || !HasExactParameterTypes(authorization, "System.String", "System.String")) return false;

            foreach (MemberAccessExpressionSyntax access in body.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (access is { Expression: IdentifierNameSyntax { Identifier.ValueText: "HttpMethod" }, Name.Identifier.ValueText: "Get" }
                    && member.Model.GetSymbolInfo(access).Symbol is not IPropertySymbol { Name: "Get", IsStatic: true }) return false;

                if (access is { Expression: IdentifierNameSyntax { Identifier.ValueText: "HttpMethod" }, Name.Identifier.ValueText: "Get" }
                    && (member.Model.GetSymbolInfo(access).Symbol is not IPropertySymbol method
                        || !IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpMethod", typeof(HttpMethod).Assembly.GetName()))) return false;

                if (access is { Expression: IdentifierNameSyntax { Identifier.ValueText: "HttpCompletionOption" }, Name.Identifier.ValueText: "ResponseHeadersRead" }
                    && (member.Model.GetSymbolInfo(access).Symbol is not IFieldSymbol { IsStatic: true } option
                        || !IsExactFrameworkType(option.ContainingType, "System.Net.Http.HttpCompletionOption", typeof(HttpCompletionOption).Assembly.GetName()))) return false;
            }

            foreach (InvocationExpressionSyntax invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (member.Model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;

                if (IsExactFrameworkConfigureAwait(method)) continue;

                if (IsExactHttpResponseFactory(method) && method.Name == "SendAsync"
                    && HasExactParameterTypes(method, "System.Net.Http.HttpRequestMessage", "System.Net.Http.HttpCompletionOption", "System.Threading.CancellationToken")
                    && AdmissionReceiver(member, invocation) is { } receiver
                    && SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(receiver).Symbol, client)) continue;

                if (method.Name == "EscapeDataString" && method.IsStatic && HasExactParameterTypes(method, "System.String")
                    && IsExactUriLibraryType(method.ContainingType, "System.Uri")) continue;

                return false;
            }

            return true;
        }

        private bool HasClosedSpendRequestOwner(AuthoredMember member, ExpressionSyntax expression)
        {
            if (!HasExactSpendSendBody(member)
                || HttpConstructionStorage(member, StripTransparentExpression(expression)) is not ILocalSymbol { Name: "request" } request
                || request.DeclaringSyntaxReferences.SingleOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
                    { Initializer.Value: BaseObjectCreationExpressionSyntax creation }
                || !IsExactFrameworkType(request.Type, "System.Net.Http.HttpRequestMessage", typeof(HttpRequestMessage).Assembly.GetName())
                || member.Model.GetOperation(creation) is not IObjectCreationOperation) return false;

            InvocationExpressionSyntax send = member.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(call => member.Model.GetSymbolInfo(call).Symbol is IMethodSymbol method && IsExactHttpResponseFactory(method));

            return AdmissionReceiver(member, send) is { } client
                && HasClosedNonOwningClientOrigin(member, client, new CleanupProvenanceContext { CollectionIndexerProof = true });
        }
    }
}
