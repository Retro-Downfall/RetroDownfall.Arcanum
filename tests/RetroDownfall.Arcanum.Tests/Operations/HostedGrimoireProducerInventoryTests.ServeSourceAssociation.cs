using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    // Test-local conditional source relationships. These never enter analyzer eligibility.
    private static bool HasServeSourceAssociation(CSharpCompilation compilation, SyntaxTree tree, bool hosted)
    {
        SemanticModel model = compilation.GetSemanticModel(tree);

        ClassDeclarationSyntax owner = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "ServeCommand");

        MethodDeclarationSyntax run = owner.Members.OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "RunHostAsync");

        static IEnumerable<SyntaxNode> ExecutableNodes(SyntaxNode body) => body.DescendantNodes(
            static node => node is not LocalFunctionStatementSyntax and not AnonymousFunctionExpressionSyntax);

        bool Same(ExpressionSyntax expression, ISymbol symbol) => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(expression).Symbol, symbol);

        IMethodSymbol? Target(InvocationExpressionSyntax call) => model.GetSymbolInfo(call).Symbol as IMethodSymbol;

        VariableDeclaratorSyntax? Variable(string name) => run.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(variable => variable.Identifier.ValueText == name);

        bool Property(ExpressionSyntax expression, ISymbol receiver, string name) => expression is MemberAccessExpressionSyntax access
            && Same(access.Expression, receiver) && model.GetSymbolInfo(access).Symbol is IPropertySymbol property && property.Name == name;

        if (Variable("builder") is not { Initializer.Value: InvocationExpressionSyntax created } builderDeclaration
            || model.GetDeclaredSymbol(builderDeclaration) is not ILocalSymbol builder
            || Target(created) is not { Name: "CreateSlimBuilder" } creator
            || creator.ContainingType.ToDisplayString() != "Microsoft.AspNetCore.Builder.WebApplication"
            || Variable("app") is not { Initializer.Value: InvocationExpressionSyntax built } appDeclaration
            || model.GetDeclaredSymbol(appDeclaration) is not ILocalSymbol app
            || built.Expression is not MemberAccessExpressionSyntax buildAccess || !Same(buildAccess.Expression, builder)
            || Target(built)?.Name != "Build") return false;

        InvocationExpressionSyntax[] calls = run.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();

        InvocationExpressionSyntax[] composition = calls.Where(call => Target(call)?.ContainingType.ToDisplayString() == "RetroDownfall.Arcanum.Api.ApiBootstrapper"
            && Target(call)?.Name == "AddArcanumApiServices" && call.Expression is MemberAccessExpressionSyntax access
            && Property(access.Expression, builder, "Services") && call.ArgumentList.Arguments is [{ Expression: { } configuration }]
            && Property(configuration, builder, "Configuration")).ToArray();

        if (composition is not [InvocationExpressionSyntax configured] || configured.Parent is not ExpressionStatementSyntax { Parent: var parent }
            || parent != run.Body || configured.SpanStart > built.SpanStart) return false;

        // The finite canonical grammar excludes explicitly changed provider/container/composition.
        if (calls.Any(call => Target(call)?.Name is "MutateServices" or "MutateBuilder" or "MutateAdapter"
                or "UseServiceProviderFactory" or "ConfigureContainer" or "UnknownHost"
                || Target(call)?.ContainingType.Name == "AlternateWorkerRegistration")
            || calls.Any(call => Target(call)?.Name == "Subscribe")
            || owner.Members.OfType<FieldDeclarationSyntax>().Any(field => field.Declaration.Variables
                .Any(variable => variable.Identifier.ValueText == "HostSubscription"))
            || calls.SelectMany(static call => call.DescendantNodes().OfType<TypeOfExpressionSyntax>())
                .Any(type => model.GetTypeInfo(type.Type).Type is INamedTypeSymbol { Name: "IOptionsMonitor" })) return false;

        if (run.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(write => Same(write.Left, app))) return false;

        if (hosted)
        {
            MethodDeclarationSyntax publicRun = owner.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "Run"
                && model.GetDeclaredSymbol(method)?.DeclaredAccessibility == Accessibility.Public);

            if (publicRun.ExpressionBody?.Expression is not InvocationExpressionSyntax forwarded
                || forwarded.ArgumentList.Arguments is not [{ Expression: IdentifierNameSyntax token }, { Expression: { } callback }]
                || model.GetSymbolInfo(callback).Symbol is not IMethodSymbol hostCallback
                || !SymbolEqualityComparer.Default.Equals(hostCallback, model.GetDeclaredSymbol(run))
                || model.GetDeclaredSymbol(publicRun.ParameterList.Parameters.Single()) is not IParameterSymbol publicToken
                || !Same(token, publicToken)) return false;

            MethodDeclarationSyntax gate = owner.Members.OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == "Run"
                && method.ParameterList.Parameters.Count == 2);

            IParameterSymbol gateToken = (IParameterSymbol)model.GetDeclaredSymbol(gate.ParameterList.Parameters[0])!;

            IParameterSymbol gateCallback = (IParameterSymbol)model.GetDeclaredSymbol(gate.ParameterList.Parameters[1])!;

            if (!ExecutableNodes(gate).OfType<InvocationExpressionSyntax>().Any(call => Same(call.Expression, gateCallback)
                    && call.ArgumentList.Arguments is [{ Expression: { } value }] && Same(value, gateToken)
                    && call.Ancestors().OfType<AwaitExpressionSyntax>().Any())) return false;

            InvocationExpressionSyntax[] starts = ExecutableNodes(run).OfType<InvocationExpressionSyntax>()
                .Where(call => Target(call)?.Name == "StartAsync").ToArray();

            return starts is [InvocationExpressionSyntax start] && start.Expression is MemberAccessExpressionSyntax started
                && Same(started.Expression, app) && start.Ancestors().OfType<AwaitExpressionSyntax>().Any();
        }

        if (Variable("actualScope") is not { Initializer.Value: InvocationExpressionSyntax scoped }
            || scoped.Expression is not MemberAccessExpressionSyntax scopeAccess || Target(scoped)?.Name != "CreateScope"
            || Variable("actualFactory") is not { Initializer.Value: InvocationExpressionSyntax resolved }
            || Target(resolved)?.Name != "GetRequiredService"
            || Target(resolved)?.TypeArguments is not [INamedTypeSymbol factoryType]
            || factoryType.ToDisplayString() != "RetroDownfall.Arcanum.Api.Intelligence.LongRestProviderClientFactory") return false;

        // A second explicitly owned factory is outside this single scoped-origin grammar.
        if (calls.Any(call => Target(call)?.Name == "ConsumeFactoryAsync"
                && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } argument
                && !Same(argument, model.GetDeclaredSymbol(Variable("actualFactory")!)!))) return false;

        ExpressionSyntax provider = scopeAccess.Expression;

        if (!Property(provider, app, "Services"))
        {
            if (model.GetSymbolInfo(provider).Symbol is not ILocalSymbol alias
                || alias.DeclaringSyntaxReferences.Single().GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } initial }
                || !Property(initial, app, "Services")
                || run.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(write => Same(write.Left, alias))) return false;
        }

        ILocalSymbol scope = (ILocalSymbol)model.GetDeclaredSymbol(Variable("actualScope")!)!;

        return resolved.Expression is MemberAccessExpressionSyntax resolvedAccess && Property(resolvedAccess.Expression, scope, "ServiceProvider");
    }

    // One connection per send is a statement about assignments, not about text: a comment is not a setting, a later assignment or a compound
    // assignment overrides one, and a conditional one is optional. The method must assign the property exactly once, as a statement of its
    // own at the top of the method body or of its protected block, and the value must be TimeSpan.Zero.
    private static void AssertSingleZeroAssignment(MethodDeclarationSyntax method, string property)
    {
        AssignmentExpressionSyntax[] assignments = method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } && name == property)
            .ToArray();

        AssignmentExpressionSyntax assignment = Assert.Single(assignments);

        Assert.True(assignment.IsKind(SyntaxKind.SimpleAssignmentExpression), property + " must be assigned, not adjusted.");

        Assert.True(assignment.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "handler" } }, property + " must be set on the handler.");

        Assert.True(assignment.Right is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: "TimeSpan" },
            Name.Identifier.ValueText: "Zero",
        }, property + " must be set to TimeSpan.Zero.");

        ExpressionStatementSyntax statement = Assert.IsType<ExpressionStatementSyntax>(assignment.Parent);

        BlockSyntax block = Assert.IsType<BlockSyntax>(statement.Parent);

        Assert.True(block == method.Body || block.Parent is TryStatementSyntax protector && protector.Block == block && protector.Parent == method.Body,
            property + " must be set unconditionally, in the method body or its protected block.");
    }

    private static void AssertOwnedLongRestTransportSource(CSharpCompilation api, CSharpCompilation infrastructure)
    {
        SyntaxTree tree = Assert.Single(api.SyntaxTrees, static tree => tree.FilePath == "src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderClientFactory.cs");

        ClassDeclarationSyntax factory = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>(),
            static type => type.Identifier.ValueText == "LongRestProviderClientFactory");

        Assert.Contains(factory.Modifiers, static token => token.ValueText == "sealed");

        ConstructorDeclarationSyntax constructor = Assert.Single(factory.Members.OfType<ConstructorDeclarationSyntax>(),
            static constructor => constructor.Modifiers.Any(static token => token.ValueText == "public"));

        Assert.Equal("IProviderApiKeyResolver", constructor.ParameterList.Parameters.Single().Type!.ToString());

        Assert.DoesNotContain(factory.DescendantNodes().OfType<IdentifierNameSyntax>(), static name => name.Identifier.ValueText == "IHttpClientFactory");

        FieldDeclarationSyntax client = Assert.Single(factory.Members.OfType<FieldDeclarationSyntax>(),
            static field => field.Declaration.Variables.Single().Identifier.ValueText == "_httpClient");

        Assert.Contains(client.Modifiers, static token => token.ValueText == "readonly");

        Assert.Contains(constructor.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            static assignment => assignment.ToString() == "_httpClient = CreateOwnedHttpClient()");

        MethodDeclarationSyntax options = Assert.Single(factory.Members.OfType<MethodDeclarationSyntax>(), static method => method.Identifier.ValueText == "Options");

        Assert.Contains("Transport = new HttpClientPipelineTransport(_httpClient)", options.ToString(), StringComparison.Ordinal);

        Assert.Contains("RetryPolicy = new ClientRetryPolicy(0)", options.ToString(), StringComparison.Ordinal);

        MethodDeclarationSyntax pool = Assert.Single(factory.Members.OfType<MethodDeclarationSyntax>(), static method => method.Identifier.ValueText == "CreateOwnedHttpClient");

        foreach (string expected in new[] { "OutboundUrlGuard.CreateProviderEgressHandler()", "new HttpClient(handler, disposeHandler: true)", "Timeout = Timeout.InfiniteTimeSpan" })
            Assert.Contains(expected, pool.ToString(), StringComparison.Ordinal);

        AssertSingleZeroAssignment(pool, "PooledConnectionLifetime");

        AssertSingleZeroAssignment(pool, "PooledConnectionIdleTimeout");

        SyntaxTree guardTree = Assert.Single(infrastructure.SyntaxTrees, static tree => tree.FilePath == "src/RetroDownfall.Arcanum.Infrastructure/Security/OutboundUrlGuard.cs");

        MethodDeclarationSyntax egress = Assert.Single(guardTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), static method => method.Identifier.ValueText == "CreateEgressHandler");

        foreach (string expected in new[] { "AllowAutoRedirect = false", "UseProxy = false", "ConnectCallback =", "EgressConnectCallbackAsync" })
            Assert.Contains(expected, egress.ToString(), StringComparison.Ordinal);

        Assert.Contains(guardTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(), static method => method.Identifier.ValueText == "CreateProviderEgressHandler"
            && method.ToString().Contains("allowPrivateAndLoopback: true", StringComparison.Ordinal));

        MethodDeclarationSyntax connect = Assert.Single(guardTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "EgressConnectCallbackAsync");

        foreach (string expected in new[] { "ResolveValidatedAddressesAsync(", "if (resolved.IsFailure)",
            "foreach (IPAddress address in pinned)", "if (IsBlockedAddress(address, allowPrivateAndLoopback))",
            "await socket.ConnectAsync(new IPEndPoint(address, port), connectToken)",
            "return new NetworkStream(socket, ownsSocket: true)", "socket.Dispose()" })
            Assert.Contains(expected, connect.ToString(), StringComparison.Ordinal);

        MethodDeclarationSyntax disposal = Assert.Single(factory.Members.OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "Dispose");

        Assert.Contains("if (Interlocked.Exchange(ref _disposed, 1) == 0) _httpClient.Dispose()", disposal.ToString(), StringComparison.Ordinal);

        Assert.Contains("handler.Dispose()", pool.ToString(), StringComparison.Ordinal);

        SyntaxTree registrations = Assert.Single(api.SyntaxTrees, static tree => tree.FilePath == "src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs");

        Assert.Contains(registrations.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(), static call => call.ToString() == "services.AddScoped<LongRestProviderClientFactory>()");
    }

    // The owned-transport pin is a statement about the shipped text: one connection per send. A pooled lifetime or idle timeout that
    // keeps a connection alive past its response, or no setting at all, is refused by the same pin that accepts the shipped text.
    [Theory]
    [InlineData("handler.PooledConnectionLifetime = TimeSpan.Zero;", "handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);")]
    [InlineData("handler.PooledConnectionIdleTimeout = TimeSpan.Zero;", "handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1);")]
    [InlineData("handler.PooledConnectionLifetime = TimeSpan.Zero;", "")]
    [InlineData("handler.PooledConnectionIdleTimeout = TimeSpan.Zero;", "")]
    // A setting that is still written is not a setting that holds: a later assignment overrides it, a comment removes it, a condition makes
    // it optional, and a compound assignment moves it off zero.
    [InlineData("handler.PooledConnectionLifetime = TimeSpan.Zero;", "handler.PooledConnectionLifetime = TimeSpan.Zero; handler.PooledConnectionLifetime = TimeSpan.FromMinutes(2);")]
    [InlineData("handler.PooledConnectionIdleTimeout = TimeSpan.Zero;", "handler.PooledConnectionIdleTimeout = TimeSpan.Zero; handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1);")]
    [InlineData("handler.PooledConnectionLifetime = TimeSpan.Zero;", "// handler.PooledConnectionLifetime = TimeSpan.Zero;")]
    [InlineData("handler.PooledConnectionIdleTimeout = TimeSpan.Zero;", "// handler.PooledConnectionIdleTimeout = TimeSpan.Zero;")]
    [InlineData("handler.PooledConnectionLifetime = TimeSpan.Zero;", "if (handler.EnableMultipleHttp2Connections) handler.PooledConnectionLifetime = TimeSpan.Zero;")]
    [InlineData("handler.PooledConnectionIdleTimeout = TimeSpan.Zero;", "if (handler.EnableMultipleHttp2Connections) handler.PooledConnectionIdleTimeout = TimeSpan.Zero;")]
    [InlineData("handler.PooledConnectionLifetime = TimeSpan.Zero;", "handler.PooledConnectionLifetime = TimeSpan.Zero; handler.PooledConnectionLifetime += TimeSpan.FromMinutes(2);")]
    [InlineData("handler.PooledConnectionIdleTimeout = TimeSpan.Zero;", "handler.PooledConnectionIdleTimeout = TimeSpan.Zero; handler.PooledConnectionIdleTimeout += TimeSpan.FromMinutes(1);")]
    public void TheOwnedTransportSourcePinRefusesAPooledConnectionThatOutlivesItsSend(string shipped, string replacement)
    {
        const string factoryPath = "src/RetroDownfall.Arcanum.Api/Intelligence/LongRestProviderClientFactory.cs";

        IReadOnlyList<CSharpCompilation> production = HostedGrimoireProducerInventory.ProductionCompilations;

        CSharpCompilation api = Assert.Single(production, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Api");

        CSharpCompilation infrastructure = Assert.Single(production, static compilation => compilation.AssemblyName == "RetroDownfall.Arcanum.Infrastructure");

        SyntaxTree tree = Assert.Single(api.SyntaxTrees, static tree => tree.FilePath == factoryPath);

        string text = tree.GetText().ToString();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(shipped)));

        // The shipped text is accepted by the very pin that refuses its mutations.
        AssertOwnedLongRestTransportSource(api, infrastructure);

        SyntaxTree mutated = tree.WithChangedText(Microsoft.CodeAnalysis.Text.SourceText.From(text.Replace(shipped, replacement, StringComparison.Ordinal)));

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertOwnedLongRestTransportSource(api.ReplaceSyntaxTree(tree, mutated), infrastructure));
    }

    private static void AssertCanonicalWorkerScopedSource(CSharpCompilation infrastructure)
    {
        SyntaxTree registrations = Assert.Single(infrastructure.SyntaxTrees, static tree => tree.FilePath
            == "src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs");

        Assert.Contains(registrations.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
            static call => call.ToString() == "services.AddInstallationResetRecoveryAwareHostedService<LongRestSweepHostedService>()");

        MethodDeclarationSyntax registration = Assert.Single(registrations.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "AddInstallationResetRecoveryAwareHostedService");

        foreach (string expected in new[] { "services.TryAddSingleton<TService>()", "services.AddHostedService",
            "new InstallationResetRecoveryAwareHostedService<TService>", "sp.GetRequiredService<TService>()",
            "sp.GetService<InstallationResetApiAdmission>()" })
            Assert.Contains(expected, registration.ToString(), StringComparison.Ordinal);

        SyntaxTree wrapper = Assert.Single(infrastructure.SyntaxTrees, static tree => tree.FilePath
            == "src/RetroDownfall.Arcanum.Infrastructure/Hosting/InstallationResetRecoveryAwareHostedService.cs");

        MethodDeclarationSyntax start = Assert.Single(wrapper.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "StartAsync");

        Assert.Contains("admission?.ActiveRecovery is not null", start.ToString(), StringComparison.Ordinal);

        Assert.Contains("await service.StartAsync(cancellationToken).ConfigureAwait(false)", start.ToString(), StringComparison.Ordinal);

        SyntaxTree worker = Assert.Single(infrastructure.SyntaxTrees, static tree => tree.FilePath
            == "src/RetroDownfall.Arcanum.Infrastructure/Hosting/LongRestSweepHostedService.cs");

        MethodDeclarationSyntax run = Assert.Single(worker.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>(),
            static method => method.Identifier.ValueText == "RunOneAsync");

        foreach (string expected in new[] { "await using IGrimoireWorkLease lease = admitted!",
            "await using AsyncServiceScope scope = scopes.CreateAsyncScope()",
            "scope.ServiceProvider.GetRequiredService<ILongRestAdmittedSweep>()",
            "await sweep.ResumeAdmittedAsync(operation, lease, ct).ConfigureAwait(false)" })
            Assert.Contains(expected, run.ToString(), StringComparison.Ordinal);

        // The lease travels by value: no holder is resolved and nothing is bound.
        Assert.DoesNotContain("Bind(", run.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("LongRestWorkAdmission", run.ToString(), StringComparison.Ordinal);
    }

    private void AssertConsumedHistoricalFactory(string shape)
    {
        bool opaque = shape is "explicit-custom-owner" or "helper-custom-owner" or "other-provider-scope"
            or "same-helper-good-bad" or "same-helper-bad-good";

        // These are explicitly invoked historical consumers. They do not predict DI hook activation.
        string before = shape == "same-helper-bad-good" ? "" : "Known(); ";

        string after = shape == "same-helper-bad-good" ? " Known();" : "";

        string body = opaque
            ? before + "IHttpClientFactory supplied = Unknown(); Consume(supplied);" + after
            : "Consume(new HistoricalFactory());";

        string source = "using System.Net.Http; using OpenAI; using System.ClientModel; using System.ClientModel.Primitives; "
            + R2Source("HistoricalFactoryProbe.Run();", "static class HistoricalFactoryProbe { public static void Run() { "
                + "System.IO.File.Exists(\"historical-consumer\"); " + body + " } "
                + "private static void Known() { Consume(new HistoricalFactory()); System.IO.File.Exists(\"historical-known-control\"); } "
                + "private static extern IHttpClientFactory Unknown(); "
                + "private static void Consume(IHttpClientFactory supplied) { OpenAIClientOptions options = new() { "
                + "Endpoint = new Uri(\"https://provider.invalid/v1\"), "
                + "Transport = new HttpClientPipelineTransport(supplied.CreateClient(\"LongRestProvider\")), "
                + "RetryPolicy = new ClientRetryPolicy(0) }; "
                + "_ = new OpenAI.Embeddings.EmbeddingClient(\"model\", new ApiKeyCredential(\"test-key\"), options); } } "
                + "sealed class HistoricalFactory : IHttpClientFactory { public HttpClient CreateClient(string name) { "
                + "HistoricalHook.Apply(); return new(); } } "
                + "static class HistoricalHook { public static void Apply() { System.IO.File.Delete(\"historical-" + shape + "\"); } }");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        InvocationExpressionSyntax call = Assert.Single(compilation.SyntaxTrees.SelectMany(static tree => tree.GetRoot().DescendantNodes()).OfType<InvocationExpressionSyntax>(),
            static invocation => invocation.ToString() == "supplied.CreateClient(\"LongRestProvider\")");

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.EnclosingType == "HistoricalFactoryProbe" && site.Member == "Run" && site.Callee == "System.IO.File.Exists");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_ROOT_UNRESOLVED"
            or "HOSTED_EVALUATION_ENVIRONMENT_LIMIT_EXCEEDED" or "HOSTED_TRAVERSAL_STATE_LIMIT_EXCEEDED");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "OpenAI.Embeddings.EmbeddingClient..ctor");

        if (opaque)
        {
            Assert.Contains(result.Items, static site => site.EnclosingType == "HistoricalFactoryProbe" && site.Member == "Known" && site.Callee == "System.IO.File.Exists");

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Identity == call.SyntaxTree.FilePath + ":" + call.SpanStart
                && diagnostic.Detail == "System.Net.Http.IHttpClientFactory.CreateClient");
        }

        {
            HostedProducerSite effect = Assert.Single(result.Items, static site => site.EnclosingType == "HistoricalHook"
                && site.Member == "Apply" && site.Callee == "System.IO.File.Delete");

            Assert.Null(effect.WorkFrontierCapsuleId);

            Assert.Null(effect.EffectFrontierCapsuleId);

            foreach (string code in new[] { "HOSTED_SITE_WORK_FRONTIER_MISSING", "HOSTED_SITE_EFFECT_FRONTIER_MISSING" })
                Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Detail == effect.Callee
                    && diagnostic.Identity.EndsWith(effect.OperationId[effect.OperationId.LastIndexOf("/site@", StringComparison.Ordinal)..], StringComparison.Ordinal));
        }

        output.WriteLine("Historical consumed factory source control: " + shape);
    }
}
