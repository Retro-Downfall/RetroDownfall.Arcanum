using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("chat", false)]
    [InlineData("embedding", false)]
    [InlineData("chat", true)]
    [InlineData("embedding", true)]
    public void ExactSdkConstructionDoesNotExecuteItsStoredHttpClient(string kind, bool factory)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSdkConstructionBoundary(kind, factory);

        string constructor = kind == "chat" ? "OpenAI.Chat.ChatClient..ctor" : "OpenAI.Embeddings.EmbeddingClient..ctor";

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == constructor);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("embedding")]
    public void UnknownHttpFactoryRetainsItsOwnExecutionObligation(string kind)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverSdkConstructionBoundary(kind, factory: true);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.IHttpClientFactory.CreateClient");
    }

    private HostedProducerDiscovery<HostedProducerSite> DiscoverSdkConstructionBoundary(string kind, bool factory)
    {
        string client = kind == "chat" ? "OpenAI.Chat.ChatClient" : "OpenAI.Embeddings.EmbeddingClient";

        string source = "using System; using System.Net.Http; using System.Threading; using System.Threading.Tasks; "
            + "using Microsoft.Extensions.Hosting; using OpenAI; using System.ClientModel; using System.ClientModel.Primitives; "
            + "public sealed class Worker(" + (factory ? "IHttpClientFactory clients" : "HttpClient ownedClient") + ") : IHostedService { "
            + "private OpenAIClientOptions Options() => new() { Endpoint = new Uri(\"https://provider.invalid/v1\"), "
            + "Transport = new HttpClientPipelineTransport(" + (factory ? "clients.CreateClient(\"LongRestProvider\")" : "ownedClient")
            + "), RetryPolicy = new ClientRetryPolicy(0) }; "
            + "public Task StartAsync(CancellationToken token) { System.IO.File.Delete(\"constructor-boundary-marker\"); "
            + "OpenAIClientOptions options = Options(); _ = new " + client + "(\"model\", new ApiKeyCredential(\"fixture-key\"), options); "
            + "return Task.CompletedTask; } public Task StopAsync(CancellationToken token) => Task.CompletedTask; }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);

        SemanticModel model = compilation.GetSemanticModel(tree);

        ObjectCreationExpressionSyntax sdk = Assert.Single(tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            creation => model.GetSymbolInfo(creation).Symbol is IMethodSymbol constructor
                && constructor.ContainingType.ToDisplayString() == client);

        IObjectCreationOperation creation = Assert.IsAssignableFrom<IObjectCreationOperation>(model.GetOperation(sdk));

        Assert.Equal(3, creation.Arguments.Length);

        Assert.Equal("OpenAI", creation.Constructor!.ContainingAssembly.Identity.Name);

        if (factory)
        {
            InvocationExpressionSyntax invocation = Assert.Single(tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => model.GetSymbolInfo(call).Symbol is IMethodSymbol method && method.Name == "CreateClient");

            IInvocationOperation operation = Assert.IsAssignableFrom<IInvocationOperation>(model.GetOperation(invocation));

            Assert.Equal("System.Net.Http.IHttpClientFactory", operation.TargetMethod.ContainingType.ToDisplayString());

            Assert.False(operation.TargetMethod.Locations.Any(static location => location.IsInSource));
        }

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.EnclosingType == "Worker" && site.Member == "StartAsync"
            && site.Callee == "System.IO.File.Delete");

        output.WriteLine($"R199_SDK_BOUNDARY={kind}|factory={factory}|compilerConstructorFactoryAndMarker=passed");

        return result;
    }
}
