using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("direct-cast", true)]
    [InlineData("returned-lease", true)]
    [InlineData("unknown-inner", false)]
    [InlineData("override", false)]
    [InlineData("unknown-factory", false)]
    public void Returned_chat_lease_conditional_cast_cleanup_preserves_closed_factory_origins(string shape, bool closed)
    {
        string inner = shape == "unknown-inner" ? "Input.Client"
            : "new OpenAI.Chat.ChatClient(\"model\", new ApiKeyCredential(\"fixture\"), new OpenAIClientOptions { Endpoint = new Uri(\"https://fixture.invalid/v1\"), Transport = new HttpClientPipelineTransport(http), NetworkTimeout = Timeout.InfiniteTimeSpan, RetryPolicy = new ClientRetryPolicy(0) }).AsIChatClient()";

        string cleanup = shape == "override" ? "protected override void Dispose(bool disposing) { System.IO.File.Delete(\"authored-sdk-cleanup\"); base.Dispose(disposing); }" : "";

        string factory = shape == "unknown-factory" ? "Input.Factory" : "new Factory(http)";

        string body = shape == "direct-cast"
            ? "IChatClient client = new Wrapper(" + inner + "); (client as IDisposable)?.Dispose();"
            : "IFactory factory = " + factory + "; Outcome<Lease> result = await factory.ResolveAsync(); if (!result.IsSuccess) return; using Lease lease = result.Value;";

        string source = "using System; using System.Net.Http; using System.Threading; using System.Threading.Tasks; using Microsoft.Extensions.Hosting; using Microsoft.Extensions.AI; using OpenAI; using System.ClientModel; using System.ClientModel.Primitives; "
            + "public sealed class Worker(HttpClient http) : IHostedService { public async Task StartAsync(CancellationToken token) { " + body + " await Task.CompletedTask; } public Task StopAsync(CancellationToken token) => Task.CompletedTask; } "
            + "internal interface IFactory { Task<Outcome<Lease>> ResolveAsync(); } internal sealed class Factory(HttpClient http) : IFactory { public async Task<Outcome<Lease>> ResolveAsync() { await Task.CompletedTask; return new(new Lease(new Wrapper(" + inner + "))); } } "
            + "internal sealed record Outcome<T>(T Value) { public bool IsSuccess => true; } "
            + "internal sealed class Lease : IDisposable { public Lease(IChatClient client) { Client = client; } public IChatClient Client { get; } public void Dispose() { (Client as IDisposable)?.Dispose(); } } "
            + "internal static class Input { internal static IChatClient Client = null!; internal static IFactory Factory = null!; } "
            + "internal sealed class Wrapper(IChatClient inner) : DelegatingChatClient(inner) { " + cleanup + " }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (closed) Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
        else Assert.True(result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code is "HOSTED_DISPOSAL_TARGET_UNRESOLVED" or "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_SITE_UNCLASSIFIED"));
    }

    [Fact]
    public void Foreign_linked_list_membership_cannot_use_the_framework_read_classification()
    {
        string foreignSource = "namespace System.Collections.Generic { public sealed class LinkedList<T> { } public sealed class LinkedListNode<T>(T value) { public LinkedList<T> List { get { System.IO.File.Delete(\"foreign-membership\"); return new(); } } } }";

        CSharpCompilation foreign = CompileWithProductionReferencePack(foreignSource, "RetroDownfall.Arcanum.Api").WithAssemblyName("Foreign.LinkedList");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        string source = "extern alias counterfeit; using System.Threading; using System.Threading.Tasks; using Microsoft.Extensions.Hosting; "
            + "public sealed class Worker : IHostedService { public Task StartAsync(CancellationToken token) { _ = new counterfeit::System.Collections.Generic.LinkedListNode<object>(new object()).List; return Task.CompletedTask; } public Task StopAsync(CancellationToken token) => Task.CompletedTask; }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.LinkedListNode`1.List");
    }

    [Theory]
    [InlineData("chat", "closed")]
    [InlineData("embedding", "closed")]
    [InlineData("chat", "unknown-inner")]
    [InlineData("embedding", "unknown-inner")]
    [InlineData("chat", "override")]
    [InlineData("embedding", "override")]
    public void Ai_delegating_cleanup_preserves_the_actual_inner_and_authored_overrides(string kind, string shape)
    {
        bool chat = kind == "chat";

        string contract = chat ? "IChatClient" : "IEmbeddingGenerator<string, Embedding<float>>";

        string parent = chat ? "DelegatingChatClient" : "DelegatingEmbeddingGenerator<string, Embedding<float>>";

        string sdk = chat ? "OpenAI.Chat.ChatClient" : "OpenAI.Embeddings.EmbeddingClient";

        string adapter = chat ? "AsIChatClient()" : "AsIEmbeddingGenerator()";

        string inner = shape == "unknown-inner" ? "Input.Current"
            : "new " + sdk + "(\"model\", new ApiKeyCredential(\"fixture\"), new OpenAIClientOptions { Endpoint = new Uri(\"https://fixture.invalid/v1\"), Transport = new HttpClientPipelineTransport(http), NetworkTimeout = Timeout.InfiniteTimeSpan, RetryPolicy = new ClientRetryPolicy(0) })." + adapter;

        string cleanup = shape == "override" ? "protected override void Dispose(bool disposing) { System.IO.File.Delete(\"authored-sdk-cleanup\"); base.Dispose(disposing); }" : "";

        string source = "using System; using System.Net.Http; using System.Threading; using System.Threading.Tasks; using Microsoft.Extensions.Hosting; using Microsoft.Extensions.AI; using OpenAI; using System.ClientModel; using System.ClientModel.Primitives; "
            + "public sealed class Worker(HttpClient http) : IHostedService { public Task StartAsync(CancellationToken token) { using IDisposable wrapped = new Wrapper(" + inner + "); System.IO.File.Exists(\"cleanup-control\"); return Task.CompletedTask; } public Task StopAsync(CancellationToken token) => Task.CompletedTask; } "
            + "internal static class Input { internal static " + contract + " Current = null!; } "
            + "internal sealed class Wrapper(" + contract + " inner) : " + parent + "(inner) { " + cleanup + " }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (shape == "closed")
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.StartsWith("Microsoft.Extensions.AI.Delegating", StringComparison.Ordinal));
        }
        else if (shape == "unknown-inner")
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
        }
        else
        {
            Assert.True(result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
                || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Linked_list_node_membership_is_mechanical_but_an_authored_lookalike_remains_an_effect(bool lookalike)
    {
        string source = "using System; using System.Threading; using System.Threading.Tasks; using Microsoft.Extensions.Hosting; "
            + "public sealed class Worker : IHostedService { public Task StartAsync(CancellationToken token) { var node = new System.Collections.Generic.LinkedListNode<object>(new object()); _ = node.List; return Task.CompletedTask; } public Task StopAsync(CancellationToken token) => Task.CompletedTask; }"
            + (lookalike ? " namespace System.Collections.Generic { internal sealed class LinkedListNode<T>(T value) { public object List { get { System.IO.File.Delete(\"lookalike-membership\"); return new object(); } } } }" : "");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        if (lookalike) Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete");
        else Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.LinkedListNode`1.List");
    }
}
