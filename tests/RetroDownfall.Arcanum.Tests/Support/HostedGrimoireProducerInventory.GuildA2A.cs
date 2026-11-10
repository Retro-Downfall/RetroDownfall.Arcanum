using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using Microsoft.CodeAnalysis.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    internal static IReadOnlyList<(string Constructor, bool Reviewed, string RuntimeTypes)> ProbeA2AProductionConstruction(
        IReadOnlyList<Microsoft.CodeAnalysis.CSharp.CSharpCompilation> compilations) =>
        new ProducerGraph(compilations, DefaultEvaluationEnvironmentMaximumMembers, DefaultEvaluationEnvironmentMaximumDepth,
            DefaultTraversalMaximumDepth, DefaultMaximumAnalyzedStatesPerRoot, false).ProbeA2AProductionConstruction();

    private sealed partial class ProducerGraph
    {
        internal IReadOnlyList<(string Constructor, bool Reviewed, string RuntimeTypes)> ProbeA2AProductionConstruction()
        {
            AuthoredMember member = AuthoredMembers.Single(candidate => TypeKey(candidate.Symbol.ContainingType)
                == "RetroDownfall.Arcanum.Infrastructure.A2A.A2AClientService" && candidate.Symbol.Name == "ConnectAsync");

            List<(string Constructor, bool Reviewed, string RuntimeTypes)> results = [];

            foreach (BaseObjectCreationExpressionSyntax node in member.Syntax.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (member.Model.GetOperation(node) is not IObjectCreationOperation { Constructor: { } constructor } creation
                    || TypeKey(constructor.ContainingType) is not ("A2A.A2AClient" or "A2A.A2AHttpJsonClient")) continue;

                ExpressionSyntax uri = (ExpressionSyntax)creation.Arguments[0].Value.Syntax;

                CleanupProvenance provenance = CleanupProvenanceOf(member, uri, new CleanupProvenanceContext { CollectionIndexerProof = true });

                results.Add((constructor.GetDocumentationCommentId()!, IsReviewedA2ABuiltinConstruction(constructor, node, member),
                    "complete=" + provenance.Complete + "; " + string.Join(", ", provenance.Candidates.Select(candidate =>
                        candidate.Type.ToDisplayString() + " @ " + candidate.Type.ContainingAssembly.Identity))));
            }

            return results;
        }

        // A2A 1.0.0-preview2: these sealed DTO members only store fields, construct
        // other sealed DTOs/lists, or select a discriminant from those fields.
        // Every receiver, initializer and argument still enters authored traversal.
        private static readonly HashSet<string> ReviewedA2AReads = new(StringComparer.Ordinal)
        {
            "A2A.AgentCard.Skills", "A2A.AgentCard.DefaultOutputModes", "A2A.AgentCard.SupportedInterfaces", "A2A.AgentCard.Capabilities",
            "A2A.AgentSkill.Id", "A2A.AgentSkill.OutputModes",
            "A2A.AgentInterface.Url", "A2A.AgentInterface.ProtocolBinding", "A2A.AgentInterface.ProtocolVersion",
            "A2A.AgentCapabilities.PushNotifications", "A2A.AgentCapabilities.Streaming",
            "A2A.AgentTask.Id", "A2A.AgentTask.ContextId", "A2A.AgentTask.Status", "A2A.AgentTask.Artifacts", "A2A.AgentTask.Metadata",
            "A2A.Message.TaskId", "A2A.Message.ContextId", "A2A.Message.Parts", "A2A.Message.Metadata",
            "A2A.TaskStatus.State", "A2A.TaskStatus.Message", "A2A.Artifact.Parts",
            "A2A.SendMessageResponse.Task", "A2A.SendMessageResponse.Message", "A2A.SendMessageResponse.PayloadCase",
            "A2A.StreamResponse.Task", "A2A.StreamResponse.StatusUpdate", "A2A.StreamResponse.ArtifactUpdate", "A2A.StreamResponse.Message", "A2A.StreamResponse.PayloadCase",
            "A2A.TaskStatusUpdateEvent.Status", "A2A.TaskStatusUpdateEvent.TaskId", "A2A.TaskStatusUpdateEvent.ContextId",
            "A2A.TaskArtifactUpdateEvent.TaskId", "A2A.TaskArtifactUpdateEvent.ContextId", "A2A.Part.Text", "A2A.Part.ContentCase",
        };

        private static readonly HashSet<string> ReviewedA2AWrites = new(StringComparer.Ordinal)
        {
            "A2A.Message.Role", "A2A.Message.MessageId", "A2A.Message.TaskId", "A2A.Message.ContextId", "A2A.Message.Parts", "A2A.Message.Metadata",
            "A2A.CancelTaskRequest.Id", "A2A.GetTaskRequest.Id", "A2A.SubscribeToTaskRequest.Id",
            "A2A.SendMessageRequest.Message", "A2A.SendMessageRequest.Configuration",
            "A2A.SendMessageConfiguration.ReturnImmediately", "A2A.SendMessageConfiguration.AcceptedOutputModes",
            "A2A.CreateTaskPushNotificationConfigRequest.TaskId", "A2A.CreateTaskPushNotificationConfigRequest.ConfigId", "A2A.CreateTaskPushNotificationConfigRequest.Config",
            "A2A.PushNotificationConfig.Id", "A2A.PushNotificationConfig.Url", "A2A.PushNotificationConfig.Token",
        };

        private static readonly HashSet<string> ReviewedA2AMethods = new(StringComparer.Ordinal)
        {
            "M:A2A.Message.#ctor", "M:A2A.AgentCard.#ctor", "M:A2A.CancelTaskRequest.#ctor", "M:A2A.GetTaskRequest.#ctor",
            "M:A2A.SubscribeToTaskRequest.#ctor", "M:A2A.SendMessageRequest.#ctor", "M:A2A.SendMessageConfiguration.#ctor",
            "M:A2A.CreateTaskPushNotificationConfigRequest.#ctor", "M:A2A.PushNotificationConfig.#ctor",
            "M:A2A.Part.FromText(System.String)", "M:A2A.TaskStateExtensions.IsTerminal(A2A.TaskState)",
            "M:A2A.A2AException.#ctor(System.String)",
        };

        private static bool HasExactA2AIdentity(ISymbol symbol) =>
            !symbol.Locations.Any(static location => location.IsInSource)
            && AssemblyIdentityMatches(symbol.ContainingAssembly.Identity, typeof(global::A2A.AgentCard).Assembly.GetName());

        private static bool IsReviewedA2AMethod(IMethodSymbol method) =>
            !method.IsVirtual && !method.IsAbstract && method.Arity == 0
            && method.GetDocumentationCommentId() is { } identity && ReviewedA2AMethods.Contains(identity)
            && HasExactA2AIdentity(method);

        private bool IsReviewedA2AAgentCardMetadata(IMethodSymbol method, SyntaxNode? node, AuthoredMember? member) =>
            member is not null && node is not null
            && method is { IsStatic: false, IsVirtual: false, Arity: 0, Name: "GetTypeInfo" }
            && IsExactFrameworkType(method.ContainingType, "System.Text.Json.JsonSerializerOptions", typeof(System.Text.Json.JsonSerializerOptions).Assembly.GetName())
            && HasExactParameterTypes(method, "System.Type")
            && member.Model.GetOperation(node) is IInvocationOperation
            {
                Instance: IPropertyReferenceOperation { Property: { } options },
                Arguments: [{ Value: ITypeOfOperation { TypeOperand: { } card } }],
            }
            && IsExactFrozenA2AJsonOptions(options)
            && TypeKey(card) == "A2A.AgentCard" && HasExactA2AIdentity(card);

        // The pinned initializer copies its generated context options, adds only its
        // pinned AI resolver, and calls MakeReadOnly before publication. AgentCard is
        // resolved by its first generated resolver, without invoking fallback metadata.
        private static bool IsExactFrozenA2AJsonOptions(IPropertySymbol property) =>
            property is { IsStatic: true, Name: "DefaultOptions", SetMethod: null, GetMethod.IsVirtual: false }
            && TypeKey(property.ContainingType) == "A2A.A2AJsonUtilities" && HasExactA2AIdentity(property);

        private static bool IsExactA2AUri(ITypeSymbol type) => IsExactUriLibraryType(type, "System.Uri");

        private static bool IsExactUriLibraryType(ITypeSymbol type, string name) =>
            IsExactFrameworkType(type, name, typeof(Uri).Assembly.GetName())
            || TypeKey(type) == name && !type.Locations.Any(static location => location.IsInSource)
                && AssemblyIdentityMatches(type.ContainingAssembly.Identity,
                    System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("System.Runtime")).GetName());

        private bool? a2aUriParsersClosed;

        private bool HasClosedA2AUriParserUniverse()
        {
            if (a2aUriParsersClosed is { } cached) return cached;

            a2aUriParsersClosed = false;

            HashSet<string> findings = new(StringComparer.Ordinal);

            HashSet<AssemblyIdentity> seen = [];

            foreach (var compilation in analysedCompilations)
            {
                foreach (IAssemblySymbol assembly in ScannedUniverse(compilation, analysedCompilations, ["System.Runtime", "System.Private.Uri"], findings))
                {
                    if (!seen.Add(assembly.Identity)) continue;

                    foreach (INamedTypeSymbol type in SourceTypes(assembly.GlobalNamespace))
                    {
                        bool parser = false;

                        HashSet<INamedTypeSymbol> parents = new(SymbolEqualityComparer.Default);

                        for (INamedTypeSymbol? parent = type; parent is not null; parent = parent.BaseType)
                        {
                            if (parent.TypeKind == TypeKind.Error || !parents.Add(parent.OriginalDefinition)) return false;

                            parser |= IsExactUriLibraryType(parent, "System.UriParser");
                        }

                        if (!parser) continue;

                        // These exact BCL definitions implement URI formatting without
                        // authored hooks. Any subclass, including one of GenericUriParser,
                        // remains unknown: an exact Uri may consult a registered parser.
                        string name = TypeKey(type);

                        if (name is not ("System.UriParser" or "System.UriParser.BuiltInUriParser" or "System.GenericUriParser"
                            or "System.HttpStyleUriParser" or "System.FtpStyleUriParser" or "System.FileStyleUriParser"
                            or "System.NewsStyleUriParser" or "System.GopherStyleUriParser" or "System.LdapStyleUriParser"
                            or "System.NetPipeStyleUriParser" or "System.NetTcpStyleUriParser")
                            || !IsExactUriLibraryType(type, name)) return false;
                    }
                }
            }

            return (a2aUriParsersClosed = findings.Count == 0).Value;
        }

        private bool IsReviewedA2ABuiltinConstruction(IMethodSymbol method, SyntaxNode? node, AuthoredMember? member)
        {
            if (member is null || node is null || method.MethodKind != MethodKind.Constructor
                || TypeKey(method.ContainingType) is not ("A2A.A2AClient" or "A2A.A2AHttpJsonClient")
                || !HasExactA2AIdentity(method) || !method.ContainingType.IsSealed
                || !HasExactParameterTypes(method, "System.Uri", "System.Net.Http.HttpClient")
                || !HasClosedA2AUriParserUniverse()
                || member.Model.GetOperation(node) is not IObjectCreationOperation creation
                || creation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 0)?.Value.Syntax is not ExpressionSyntax uri) return false;

            // Both constructors call Uri.ToString. Only exact Uri constructions close
            // that virtual dispatch; arbitrary or derived Uri inputs remain refused.
            ILocalSymbol? local = member.Model.GetSymbolInfo(StripTransparentExpression(uri)).Symbol as ILocalSymbol;

            if (local is not null && !IsStableAcrossAdmissionCleanupBackEdges(member, uri, local)) return false;

            CleanupProvenance provenance = CleanupProvenanceOf(member, uri, new CleanupProvenanceContext { CollectionIndexerProof = true });

            if (provenance.Complete && provenance.Candidates.Count != 0
                && provenance.Candidates.All(candidate => IsExactA2AUri(candidate.Type))) return true;

            // A callback invoked before this local's declaration cannot capture it.
            // The general cleanup proof includes those earlier calls conservatively;
            // this certificate starts at the fresh exact Uri's declaration instead.
            return local is not null && IsExactA2AUri(local.Type)
                && local.DeclaringSyntaxReferences is [SyntaxReference declaration]
                && declaration.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: BaseObjectCreationExpressionSyntax initializer } variable
                && member.Syntax.Span.Contains(variable.Span) && variable.Span.End < uri.SpanStart
                && member.Model.GetOperation(initializer) is IObjectCreationOperation { Type: { } created }
                && IsExactA2AUri(created)
                && SymbolIsStableBetween(member, local, variable.Span.End, uri);
        }

        private static bool IsReviewedA2ABuiltinCleanup(ITypeSymbol exactType, IMethodSymbol cleanup) =>
            cleanup is { Name: "Dispose", Parameters.Length: 0, ReturnsVoid: true, IsStatic: false }
            && TypeKey(exactType) is "A2A.A2AClient" or "A2A.A2AHttpJsonClient"
            && exactType is INamedTypeSymbol { IsSealed: true }
            && HasExactA2AIdentity(exactType) && HasExactA2AIdentity(cleanup)
            && SymbolEqualityComparer.Default.Equals(exactType, cleanup.ContainingType);

        private static bool IsReviewedA2ABclStorageMethod(IMethodSymbol method)
        {
            string? identity = method.GetDocumentationCommentId();

            if (method is { MethodKind: MethodKind.Constructor, Parameters.Length: 0 }
                && (IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName())
                    || IsExactFrameworkType(method.ContainingType, "System.Threading.SpinWait", typeof(SpinWait).Assembly.GetName()))) return true;

            return !method.IsStatic && !method.IsAbstract && !method.IsVirtual && method.Arity == 0
                && (IsExactFrameworkType(method.ContainingType, "System.Net.Http.Headers.HttpHeaders", typeof(System.Net.Http.Headers.HttpHeaders).Assembly.GetName())
                        && identity is "M:System.Net.Http.Headers.HttpHeaders.Remove(System.String)"
                            or "M:System.Net.Http.Headers.HttpHeaders.TryAddWithoutValidation(System.String,System.String)"
                    || IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpResponseMessage", typeof(HttpResponseMessage).Assembly.GetName())
                        && identity == "M:System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode"
                    || IsExactFrameworkType(method.ContainingType, "System.Text.Json.JsonElement", typeof(System.Text.Json.JsonElement).Assembly.GetName())
                        && identity is "M:System.Text.Json.JsonElement.TryGetInt64(System.Int64@)" or "M:System.Text.Json.JsonElement.TryGetDecimal(System.Decimal@)"
                    || IsExactFrameworkType(method.ContainingType, "System.Threading.SpinWait", typeof(SpinWait).Assembly.GetName())
                        && identity == "M:System.Threading.SpinWait.SpinOnce");
        }

        private static bool IsReviewedA2AHttpStorageProperty(IPropertySymbol property, bool mutation) =>
            property is { IsStatic: false, IsIndexer: false }
            && (mutation ? property.SetMethod : property.GetMethod) is { IsVirtual: false, IsAbstract: false }
            && (!mutation && IsExactHttpResponseContent(property)
                || !mutation && property.Name == "Headers"
                    && IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpContent", typeof(HttpContent).Assembly.GetName())
                || property.Name == (mutation ? "MaxResponseContentBufferSize" : "DefaultRequestHeaders")
                    && IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName()));

        private static bool IsReviewedA2AProperty(IPropertySymbol property, bool mutation) =>
            !mutation && IsExactFrozenA2AJsonOptions(property)
            || property.ContainingType.IsSealed && !property.IsStatic && !property.IsIndexer
            && (mutation ? ReviewedA2AWrites : ReviewedA2AReads).Contains(Normalize(property))
            && (mutation ? property.SetMethod : property.GetMethod) is { IsVirtual: false, IsAbstract: false }
            && HasExactA2AIdentity(property);

        // The exact request surface remains a provider effect, including lazy streams.
        // This never makes a network operation or a caller-provided implementation pure.
        private static bool IsExactA2ANetworkMethod(IMethodSymbol method)
        {
            if (!HasExactA2AIdentity(method) || method.IsStatic || method.Arity != 0
                || TypeKey(method.ContainingType) != "A2A.IA2AClient"
                || method.Parameters is not [{ RefKind: RefKind.None } request, { RefKind: RefKind.None } cancellation]
                || !IsExactFrameworkType(cancellation.Type, "System.Threading.CancellationToken", typeof(CancellationToken).Assembly.GetName())) return false;

            string? requestType = method.Name switch
            {
                "SendMessageAsync" => "A2A.SendMessageRequest",
                "GetTaskAsync" => "A2A.GetTaskRequest",
                "CancelTaskAsync" => "A2A.CancelTaskRequest",
                "SubscribeToTaskAsync" => "A2A.SubscribeToTaskRequest",
                "CreateTaskPushNotificationConfigAsync" => "A2A.CreateTaskPushNotificationConfigRequest",
                _ => null,
            };

            return requestType is not null && TypeKey(request.Type) == requestType && HasExactA2AIdentity(request.Type);
        }

        private readonly Dictionary<(BoundTypeIdentity Contract, string Method), AuthoredMember[]> a2aAuthoredDispatchTargets = [];

        private bool IsA2ASourceDispatchContract(IMethodSymbol method, Compilation consumingCompilation) =>
            IsExactA2ANetworkMethod(method)
            || IsExactGuildMetadataLookup(method)
            || IsExactAuthoredProtocolType(method.ContainingType,
                "RetroDownfall.Arcanum.Infrastructure.A2A.IA2AGuildSendingLifecycle", consumingCompilation)
            || IsExactAuthoredProtocolType(method.ContainingType,
                "RetroDownfall.Arcanum.Infrastructure.A2A.IA2AGuildDiscoveryAdmission", consumingCompilation);

        private AuthoredMember[] ResolveA2AAuthoredDispatchTargets(IMethodSymbol method, Compilation consumingCompilation)
        {
            if (!IsA2ASourceDispatchContract(method, consumingCompilation)) return [];

            var identity = (TypeIdentity(method.ContainingType, consumingCompilation), MethodKey(method));

            if (a2aAuthoredDispatchTargets.TryGetValue(identity, out AuthoredMember[]? cached)) return cached;

            // A metadata interface is outside the DI-only source contract resolver.
            // Census every authored implementation instead of hiding its body behind
            // the provider marker. Unknown external clients remain provider boundaries.
            List<AuthoredMember> implementations = [];

            foreach (var compilation in analysedCompilations)
            {
                foreach (INamedTypeSymbol type in SourceTypes(compilation.Assembly.GlobalNamespace))
                {
                    foreach (IMethodSymbol slot in type.AllInterfaces
                        .Where(contract => SameBoundType(contract, compilation, method.ContainingType, consumingCompilation)
                            && (!IsExactGuildMetadataLookup(method)
                                || IsPotentialJsonDictionary(contract, "System.Collections.Generic.IDictionary`2")))
                        .SelectMany(static contract => contract.GetMembers()).OfType<IMethodSymbol>()
                        .Where(slot => MethodKey(slot) == MethodKey(method)))
                    {
                        if (type.FindImplementationForInterfaceMember(slot) is IMethodSymbol implementation
                            && Resolve(implementation, compilation) is { } target) implementations.Add(target);
                    }
                }
            }

            AuthoredMember[] targets = implementations.DistinctBy(MemberIdentity).ToArray();

            a2aAuthoredDispatchTargets[identity] = targets;

            return targets;
        }

        // HTTP response/stream cleanup can dispatch transport code, so it is always a
        // provider effect. Its provenance must be complete and rooted in an exact BCL
        // send. Unknown responses, replaced content and unrelated streams gain no proof.
        private bool TryResolveA2AHttpCleanup(AuthoredMember member, ExpressionSyntax resource,
            ITypeSymbol staticType, string name, out ExternalCleanupResolution? external)
        {
            external = null;

            string? stage = TypeKey(staticType) switch
            {
                "System.Net.Http.HttpResponseMessage" when name == "Dispose" => "response",
                "System.IO.Stream" when name is "Dispose" or "DisposeAsync" => "stream",
                _ => null,
            };

            if (stage is null || !IsExactFrameworkType(staticType, TypeKey(staticType),
                    (stage == "response" ? typeof(HttpResponseMessage) : typeof(Stream)).Assembly.GetName())
                || !HasA2AHttpOrigin(member, resource, stage, new CleanupProvenanceContext { CollectionIndexerProof = true })) return false;

            external = new(TypeKey(staticType) + "." + name, HostedProducerSiteKind.ProviderCall);

            return true;
        }

        private bool HasA2AHttpOrigin(AuthoredMember member, ExpressionSyntax expression, string stage, CleanupProvenanceContext proof)
        {
            if (proof.RemainingWork-- <= 0) return false;

            if (stage == "response" && !HasClosedHttpResponseLocalUses(member, expression)) return false;

            CleanupValueFlow flow = CleanupValueFlowOf(member, expression, proof);

            return flow.Complete && flow.Values.Count != 0 && flow.Values.All(value =>
            {
                IOperation? operation = value.Caller.Model.GetOperation(value.Expression);

                return stage switch
                {
                    "response" => operation is IInvocationOperation call && IsExactHttpResponseFactory(call.TargetMethod),
                    "content" => operation is IPropertyReferenceOperation { Instance.Syntax: ExpressionSyntax response } content
                        && IsExactHttpResponseContent(content.Property)
                        && HasA2AHttpOrigin(value.Caller, response, "response", proof),
                    "headers" => operation is IPropertyReferenceOperation { Instance.Syntax: ExpressionSyntax content } headers
                        && IsExactHttpContentHeaders(headers.Property)
                        && HasA2AHttpOrigin(value.Caller, content, "content", proof),
                    "stream" => operation is IInvocationOperation { Instance.Syntax: ExpressionSyntax content } read
                        && IsExactHttpContentStreamFactory(read.TargetMethod)
                        && HasA2AHttpOrigin(value.Caller, content, "content", proof),
                    _ => false,
                };
            });
        }

        private static bool HasClosedHttpResponseLocalUses(AuthoredMember member, ExpressionSyntax expression)
        {
            ILocalSymbol? local = member.Model.GetSymbolInfo(expression).Symbol as ILocalSymbol;

            if (local is null && expression.AncestorsAndSelf().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } variable
                && member.Model.GetDeclaredSymbol(variable) is ILocalSymbol declared
                && IsExactFrameworkType(declared.Type, "System.Net.Http.HttpResponseMessage", typeof(HttpResponseMessage).Assembly.GetName())) local = declared;

            if (local is null) return true;

            if (HasCollectionValueReplacement(member, local)) return false;

            foreach (IdentifierNameSyntax reference in member.Model.SyntaxTree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(reference => SymbolEqualityComparer.Default.Equals(member.Model.GetSymbolInfo(reference).Symbol, local)))
            {
                ExpressionSyntax use = reference;

                while (use.Parent is ParenthesizedExpressionSyntax parentheses) use = parentheses;

                if (use.Parent is UsingStatementSyntax { Expression: { } resource } && resource == use) continue;

                if (use.Parent is not MemberAccessExpressionSyntax { Expression: { } receiver } access || receiver != use) return false;

                ISymbol? accessed = member.Model.GetSymbolInfo(access).Symbol;

                if (accessed is IPropertySymbol property && IsExactHttpResponseContent(property)) continue;

                if (accessed is IMethodSymbol method && IsExactFrameworkType(method.ContainingType,
                        "System.Net.Http.HttpResponseMessage", typeof(HttpResponseMessage).Assembly.GetName())
                    && method.Name is "EnsureSuccessStatusCode" or "Dispose" && method.Parameters.IsEmpty) continue;

                return false;
            }

            return true;
        }

        private bool IsA2AHttpProviderMethod(IMethodSymbol method, SyntaxNode node, AuthoredMember member)
        {
            if (member.Model.GetOperation(node) is not IInvocationOperation { Instance.Syntax: ExpressionSyntax receiver }) return false;

            string? stage = IsExactHttpContentStreamFactory(method) ? "content"
                : method.Name.StartsWith("Read", StringComparison.Ordinal)
                    && IsExactFrameworkType(method.ContainingType, "System.IO.Stream", typeof(Stream).Assembly.GetName()) ? "stream" : null;

            return stage is not null && HasA2AHttpOrigin(member, receiver, stage, new CleanupProvenanceContext { CollectionIndexerProof = true });
        }

        private bool IsA2AHttpProviderProperty(IPropertySymbol property, bool mutation, SyntaxNode node, AuthoredMember member) =>
            !mutation && property is { Name: "ContentLength", IsStatic: false, IsIndexer: false, GetMethod.IsVirtual: false }
            && IsExactFrameworkType(property.ContainingType, "System.Net.Http.Headers.HttpContentHeaders", typeof(System.Net.Http.Headers.HttpContentHeaders).Assembly.GetName())
            && member.Model.GetOperation(node) is IPropertyReferenceOperation { Instance.Syntax: ExpressionSyntax headers }
            && HasA2AHttpOrigin(member, headers, "headers", new CleanupProvenanceContext { CollectionIndexerProof = true });

        private static bool IsExactHttpResponseFactory(IMethodSymbol method) =>
            !method.IsStatic && !method.IsVirtual && method.Arity == 0
            && method.Name is "GetAsync" or "SendAsync"
            && IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpClient", typeof(HttpClient).Assembly.GetName())
            && method.ReturnType is INamedTypeSymbol { TypeArguments: [ITypeSymbol response] } task
            && IsExactFrameworkType(task, "System.Threading.Tasks.Task`1", typeof(Task<>).Assembly.GetName())
            && IsExactFrameworkType(response, "System.Net.Http.HttpResponseMessage", typeof(HttpResponseMessage).Assembly.GetName());

        private static bool IsExactHttpResponseContent(IPropertySymbol property) =>
            property is { Name: "Content", IsStatic: false, IsIndexer: false, GetMethod.IsVirtual: false }
            && IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpResponseMessage", typeof(HttpResponseMessage).Assembly.GetName());

        private static bool IsExactHttpContentHeaders(IPropertySymbol property) =>
            property is { Name: "Headers", IsStatic: false, IsIndexer: false, GetMethod.IsVirtual: false }
            && IsExactFrameworkType(property.ContainingType, "System.Net.Http.HttpContent", typeof(HttpContent).Assembly.GetName());

        private static bool IsExactHttpContentStreamFactory(IMethodSymbol method) =>
            method is { IsStatic: false, IsVirtual: false, Arity: 0, Name: "ReadAsStreamAsync" }
            && IsExactFrameworkType(method.ContainingType, "System.Net.Http.HttpContent", typeof(HttpContent).Assembly.GetName())
            && (method.Parameters.Length == 0 || HasExactParameterTypes(method, "System.Threading.CancellationToken"));

        private static bool IsExactHttpValueOrigin(ISymbol? symbol) => symbol switch
        {
            IMethodSymbol method => IsExactHttpResponseFactory(method) || IsExactHttpContentStreamFactory(method),
            IPropertySymbol property => IsExactHttpResponseContent(property) || IsExactHttpContentHeaders(property),
            _ => false,
        };
    }
}
