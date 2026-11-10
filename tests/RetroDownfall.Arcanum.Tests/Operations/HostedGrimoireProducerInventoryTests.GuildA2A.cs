using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using System.Reflection;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void GuildA2AProductionBuiltinConstructionClosesItsLocalUriRuntimeTypes()
    {
        var results = HostedGrimoireProducerInventory.ProbeA2AProductionConstruction(HostedGrimoireProducerInventory.ProductionCompilations);

        Assert.Equal(2, results.Count);

        Assert.All(results, result => Assert.True(result.Reviewed, result.Constructor + ": " + result.RuntimeTypes));
    }

    [Fact]
    public void GuildA2APinnedSdkDefaultJsonOptionsAreFrozenAndResolveAgentCard()
    {
        System.Text.Json.JsonSerializerOptions options = global::A2A.A2AJsonUtilities.DefaultOptions;

        Assert.Equal("1.0.0-preview2+87fd44843dd16339cdb59c2ff547fe374ac46736",
            typeof(global::A2A.AgentCard).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

        Assert.True(options.IsReadOnly);

        Assert.Throws<InvalidOperationException>(() => options.NumberHandling = options.NumberHandling);

        Assert.Equal(typeof(global::A2A.AgentCard), options.GetTypeInfo(typeof(global::A2A.AgentCard)).Type);

        var dictionary = options.GetTypeInfo(typeof(Dictionary<string, System.Text.Json.JsonElement>));

        Assert.True(dictionary.IsReadOnly);

        Assert.Empty(options.Converters);

        Assert.IsType<Dictionary<string, System.Text.Json.JsonElement>>(dictionary.CreateObject!());
    }

    [Theory]
    [InlineData("Message", "ContextId")]
    [InlineData("StreamResponse", "ArtifactUpdate")]
    [InlineData("StreamResponse", "Message")]
    [InlineData("TaskStatusUpdateEvent", "TaskId")]
    [InlineData("TaskStatusUpdateEvent", "ContextId")]
    [InlineData("TaskArtifactUpdateEvent", "TaskId")]
    [InlineData("TaskArtifactUpdateEvent", "ContextId")]
    public void GuildA2ATaskBindingReadsAreExactSdkStoredData(string type, string property)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "_ = A2AInput.Value." + property + "; await Task.CompletedTask;",
            "static class A2AInput { internal static A2A." + type + " Value = null!; }");

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A." + type + "." + property);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuildA2ANetworkClassificationStillTraversesAuthoredClientImplementations(bool inherited)
    {
        string implementation = """
            sealed class AuthoredA2AClient : A2A.IA2AClient
            {
                public Task<A2A.SendMessageResponse> SendMessageAsync(A2A.SendMessageRequest request, CancellationToken token)
                { System.IO.File.Delete("authored-a2a-send"); return Task.FromResult(new A2A.SendMessageResponse()); }
                public System.Collections.Generic.IAsyncEnumerable<A2A.StreamResponse> SendStreamingMessageAsync(A2A.SendMessageRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task<A2A.AgentTask> GetTaskAsync(A2A.GetTaskRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task<A2A.ListTasksResponse> ListTasksAsync(A2A.ListTasksRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task<A2A.AgentTask> CancelTaskAsync(A2A.CancelTaskRequest request, CancellationToken token) => throw new NotSupportedException();
                public System.Collections.Generic.IAsyncEnumerable<A2A.StreamResponse> SubscribeToTaskAsync(A2A.SubscribeToTaskRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task<A2A.TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(A2A.CreateTaskPushNotificationConfigRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task<A2A.TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(A2A.GetTaskPushNotificationConfigRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task<A2A.ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(A2A.ListTaskPushNotificationConfigRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task DeleteTaskPushNotificationConfigAsync(A2A.DeleteTaskPushNotificationConfigRequest request, CancellationToken token) => throw new NotSupportedException();
                public Task<A2A.AgentCard> GetExtendedAgentCardAsync(A2A.GetExtendedAgentCardRequest request, CancellationToken token) => throw new NotSupportedException();
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
            """;

        if (inherited)
        {
            int start = implementation.IndexOf("public Task<A2A.SendMessageResponse>", StringComparison.Ordinal);

            int end = implementation.IndexOf("public System.Collections.Generic.IAsyncEnumerable", start, StringComparison.Ordinal);

            string inheritedMethod = implementation[start..end];

            implementation = "abstract class AuthoredA2ABase { " + inheritedMethod + " } "
                + implementation.Remove(start, end - start).Replace("AuthoredA2AClient : A2A.IA2AClient",
                    "AuthoredA2AClient : AuthoredA2ABase, A2A.IA2AClient", StringComparison.Ordinal);
        }

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "A2A.IA2AClient client = new AuthoredA2AClient(); await client.SendMessageAsync(new A2A.SendMessageRequest(), token);", implementation);

        Assert.Contains(result.Items, site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == (inherited ? "AuthoredA2ABase" : "AuthoredA2AClient"));
    }

    [Fact]
    public void GuildA2AAuthoredLifecycleGenericDispatchTraversesItsImplementations()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "RetroDownfall.Arcanum.Infrastructure.A2A.IA2AGuildSendingLifecycle lifecycle = new FixtureLifecycle(); "
            + "_ = await lifecycle.AdmitAuxiliaryAsync(token => { System.IO.File.Delete(\"lifecycle-callback\"); return Task.FromResult(1); }, false, token);",
            """
            namespace RetroDownfall.Arcanum.Infrastructure.A2A
            {
                internal interface IA2AGuildSendingLifecycle
                {
                    Task<T> AdmitAuxiliaryAsync<T>(Func<CancellationToken, Task<T>> action, bool stopping, CancellationToken token);
                }
            }
            sealed class FixtureLifecycle : RetroDownfall.Arcanum.Infrastructure.A2A.IA2AGuildSendingLifecycle
            {
                public async Task<T> AdmitAuxiliaryAsync<T>(Func<CancellationToken, Task<T>> action, bool stopping, CancellationToken token)
                {
                    System.IO.File.Delete("lifecycle-dispatch");
                    return await action(token);
                }
            }
            """);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "FixtureLifecycle");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "Worker");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuildA2ADiscoveryAdmissionTraversesItsAuthoredImplementations(bool detached)
    {
        string source = """
            namespace RetroDownfall.Arcanum.Infrastructure.A2A
            {
                internal interface IA2AGuildDiscoveryAdmission
                {
                    Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token);
                }
            }
            static class FixtureDiscoveryInput
            {
                internal static RetroDownfall.Arcanum.Infrastructure.A2A.IA2AGuildDiscoveryAdmission Admission = null!;
            }
            sealed class FixtureDiscoveryAdmission : RetroDownfall.Arcanum.Infrastructure.A2A.IA2AGuildDiscoveryAdmission
            {
                public async Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
                {
                    System.IO.File.Delete("discovery-admission");
                    return await action(token);
                }
            }
            sealed class OtherDiscoveryAdmission : RetroDownfall.Arcanum.Infrastructure.A2A.IA2AGuildDiscoveryAdmission
            {
                public async Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
                {
                    System.IO.File.Delete("other-discovery-admission");
                    return await action(token);
                }
            }
            """;

        if (detached)
            source = source.Replace("return await action(token);", "_ = action(token); await Task.CompletedTask; return default!;", StringComparison.Ordinal);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "_ = await FixtureDiscoveryInput.Admission.AdmitAsync(token => { System.IO.File.Delete(\"discovery-callback\"); return Task.FromResult(1); }, token);",
            source);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "FixtureDiscoveryAdmission");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "OtherDiscoveryAdmission");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "Worker");

        Assert.Equal(detached, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));
    }

    [Theory]
    [InlineData("consumed", true)]
    [InlineData("escaped", false)]
    [InlineData("released", false)]
    public void GuildA2AStreamingProviderWorkCompletesUnderItsEnumerationFrontiers(string shape, bool owned)
    {
        const string subscribe = "A2AInput.Client.SubscribeToTaskAsync(new A2A.SubscribeToTaskRequest { Id = \"task\" }, token)";

        string body = shape switch
        {
            "consumed" => "await foreach (var update in " + subscribe + ") { _ = update.PayloadCase; }",
            "escaped" => "A2AInput.Saved = " + subscribe + ";",
            _ => "var updates = " + subscribe + "; held.Dispose(); await foreach (var update in updates) { _ = update.PayloadCase; }",
        };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(body + "await Task.CompletedTask;",
            "static class A2AInput { internal static A2A.IA2AClient Client = null!; internal static System.Collections.Generic.IAsyncEnumerable<A2A.StreamResponse> Saved = null!; }");

        Assert.Equal(!owned, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith("A2A.IA2AClient.SubscribeToTaskAsync;", StringComparison.Ordinal)));
    }

    [Fact]
    public void GuildA2AForgedNetworkInterfaceDoesNotAcquireProviderClassification()
    {
        CSharpCompilation foreign = CompileWithProductionReferencePack(
            "namespace A2A { public interface IA2AClient { System.Threading.Tasks.Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, System.Threading.CancellationToken token); } }",
            "RetroDownfall.Arcanum.Api").WithAssemblyName("ForgedA2ANetwork");

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        PortableExecutableReference reference = MetadataReference.CreateFromImage(bytes.ToArray(),
            new MetadataReferenceProperties(aliases: ["forged"]));

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias forged; "
            + R2Source(R2Admission + "await A2AInput.Client.SendMessageAsync(new A2A.SendMessageRequest(), token);",
                "static class A2AInput { internal static forged::A2A.IA2AClient Client = null!; }"),
            "RetroDownfall.Arcanum.Api").AddReferences(reference);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2ACompilation(compilation);

        Assert.DoesNotContain(result.Items, static site => site.Callee == "A2A.IA2AClient.SendMessageAsync");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A.IA2AClient.SendMessageAsync");
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("unknown-options", false)]
    [InlineData("unknown-type", false)]
    public void GuildA2AGeneratedMetadataProofRequiresTheFrozenSdkOptionsAndExactAgentCard(string shape, bool reviewed)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "_ = " + (shape == "unknown-options" ? "A2AInput.Options" : "A2A.A2AJsonUtilities.DefaultOptions")
            + ".GetTypeInfo(" + (shape == "unknown-type" ? "A2AInput.Type" : "typeof(A2A.AgentCard)") + "); await Task.CompletedTask;",
            "static class A2AInput { internal static System.Text.Json.JsonSerializerOptions Options = null!; internal static Type Type = null!; }");

        Assert.Equal(!reviewed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Text.Json.JsonSerializerOptions.GetTypeInfo"));
    }

    [Theory]
    [InlineData("A2AClient", false)]
    [InlineData("A2AClient", true)]
    [InlineData("A2AHttpJsonClient", false)]
    [InlineData("A2AHttpJsonClient", true)]
    public void GuildA2ABuiltinConstructionAndCleanupHaveClosedSdkBodies(string client, bool local)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            (local ? "Uri target = new(A2AInput.Url); " : "")
            + "using var client = new A2A." + client + "(" + (local ? "target" : "new Uri(\"https://peer.invalid\")")
            + ", new System.Net.Http.HttpClient()); await Task.CompletedTask;",
            "static class A2AInput { internal static string Url = \"https://peer.invalid\"; }");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("A2A.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("A2AClient")]
    [InlineData("A2AHttpJsonClient")]
    public void GuildA2ABuiltinConstructionDoesNotSuppressAuthoredUriFormatting(string client)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "_ = new A2A." + client + "(new A2AInputUri(), new System.Net.Http.HttpClient()); await Task.CompletedTask;",
            "sealed class A2AInputUri : Uri { internal A2AInputUri() : base(\"https://peer.invalid\") { } "
            + "public override string ToString() { System.IO.File.Delete(\"uri-format\"); return base.ToString(); } }");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A." + client + "..ctor");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuildA2ABuiltinConstructionDoesNotHideCustomUriParserHooks(bool referenced)
    {
        const string parser = "public sealed class CustomUriParser : UriParser { protected override string GetComponents(Uri uri, UriComponents components, UriFormat format) { System.IO.File.Delete(\"uri-parser\"); return \"peer.invalid\"; } }";

        string source = R2Source(R2Admission
            + "_ = new A2A.A2AClient(new Uri(\"custom://peer.invalid\"), new System.Net.Http.HttpClient()); await Task.CompletedTask;",
            referenced ? "" : parser);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        if (referenced)
        {
            CSharpCompilation foreign = CompileWithProductionReferencePack("using System; " + parser, "RetroDownfall.Arcanum.Api")
                .WithAssemblyName("ExternalUriParser");

            using MemoryStream bytes = new();

            Assert.True(foreign.Emit(bytes).Success);

            compilation = compilation.AddReferences(MetadataReference.CreateFromImage(bytes.ToArray()));
        }

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2ACompilation(compilation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A.A2AClient..ctor");
    }

    [Theory]
    [InlineData("before-declaration", true)]
    [InlineData("after-declaration", false)]
    [InlineData("captured-replacement", false)]
    [InlineData("ref-replacement", false)]
    [InlineData("back-edge-replacement", false)]
    public void GuildA2ABuiltinUriLocalRequiresStabilityFromItsDeclaration(string shape, bool reviewed)
    {
        string prefix = shape == "before-declaration" ? "Invoke(callback); " : "";

        string mutation = shape switch
        {
            "after-declaration" => "Invoke(callback); ",
            "captured-replacement" => "void Replace() { target = new AlteredUri(); } Replace(); ",
            "ref-replacement" => "Replace(ref target); ",
            _ => "",
        };

        string suffix = shape == "back-edge-replacement"
            ? "target = new AlteredUri(); if (again) goto construct; " : "";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "A2AInput.Create(static () => {}, false); await Task.CompletedTask;",
            "static class A2AInput { internal static void Create(Action callback, bool again) { "
            + prefix + "Uri target = new Uri(\"https://peer.invalid\"); " + mutation
            + (shape == "back-edge-replacement" ? "construct: " : "")
            + "_ = new A2A.A2AClient(target, new System.Net.Http.HttpClient()); " + suffix
            + "} private static void Invoke(Action callback) => callback(); "
            + "private static void Replace(ref Uri value) { value = new AlteredUri(); } } "
            + "sealed class AlteredUri : Uri { internal AlteredUri() : base(\"https://peer.invalid\") {} "
            + "public override string ToString() { System.IO.File.Delete(\"uri-format\"); return base.ToString(); } }");

        Assert.Equal(!reviewed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A.A2AClient..ctor"));
    }

    [Fact]
    public void GuildA2AMutableSdkFactoryRetainsItsCallbackObligation()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "_ = A2A.A2AClientFactory.Create(new A2A.AgentCard(), new System.Net.Http.HttpClient()); await Task.CompletedTask;");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A.A2AClientFactory.Create");
    }

    [Fact]
    public void GuildA2AExactDtoStorageHasNoProducerButItsAuthoredArgumentsStillExecute()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "var message = new A2A.Message { TaskId = A2AInput.TaskId(), Parts = [A2A.Part.FromText(\"text\")] }; "
            + "_ = message.TaskId; _ = message.Parts[0].Text; _ = new A2A.GetTaskRequest { Id = message.TaskId }; await Task.CompletedTask;",
            "static class A2AInput { internal static string TaskId() { System.IO.File.Delete(\"dto-argument\"); return \"task\"; } }");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("A2A.", StringComparison.Ordinal));

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete" && site.EnclosingType == "A2AInput");
    }

    [Theory]
    [InlineData("SendMessageAsync", "new A2A.SendMessageRequest { Message = new A2A.Message() }")]
    [InlineData("GetTaskAsync", "new A2A.GetTaskRequest { Id = \"task\" }")]
    [InlineData("CancelTaskAsync", "new A2A.CancelTaskRequest { Id = \"task\" }")]
    public void GuildA2AExactNetworkMethodsRemainProviderEffects(string method, string request)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "await A2AInput.Client." + method + "(" + request + ", token);",
            "static class A2AInput { internal static A2A.IA2AClient Client = null!; }");

        Assert.Contains(result.Items, site => site.Callee == "A2A.IA2AClient." + method
            && site.Kind == HostedProducerSiteKind.ProviderCall);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A.IA2AClient." + method);
    }

    [Theory]
    [InlineData("ForgedA2A")]
    [InlineData("A2A")]
    public void GuildA2AForgedMetadataDoesNotAcquireSdkDtoStorageProof(string assembly)
    {
        CSharpCompilation foreign = CompileWithProductionReferencePack(
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] namespace A2A { public sealed class Message { public string TaskId { get; set; } = \"\"; } }",
            "RetroDownfall.Arcanum.Api").WithAssemblyName(assembly);

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        PortableExecutableReference reference = MetadataReference.CreateFromImage(bytes.ToArray(),
            new MetadataReferenceProperties(aliases: ["forged"]));

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            "extern alias forged; " + R2Source(R2Admission
                + "var message = new forged::A2A.Message { TaskId = \"task\" }; _ = message.TaskId; await Task.CompletedTask;"),
            "RetroDownfall.Arcanum.Api").AddReferences(reference);

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2ACompilation(compilation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "A2A.Message.TaskId");
    }

    [Theory]
    [InlineData("response")]
    [InlineData("stream")]
    public void GuildA2AHttpProviderResultsHaveClassifiedCleanup(string resource)
    {
        string body = "using var response = await A2AInput.Client.GetAsync(new Uri(\"https://peer.invalid\"), token); ";

        if (resource == "stream") body += "await using var stream = await response.Content.ReadAsStreamAsync(token); _ = await stream.ReadAsync(new byte[8], token); ";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(body + "await Task.CompletedTask;",
            "static class A2AInput { internal static System.Net.Http.HttpClient Client = new(); }");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");

        Assert.Contains(result.Items, site => site.Callee == (resource == "stream"
                ? "System.IO.Stream.DisposeAsync" : "System.Net.Http.HttpResponseMessage.Dispose")
            && site.Kind == HostedProducerSiteKind.ProviderCall);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail.StartsWith("System.Net.Http.HttpContent.", StringComparison.Ordinal));

        if (resource == "stream") Assert.Contains(result.Items, static site => site.Callee == "System.IO.Stream.ReadAsync"
            && site.Kind == HostedProducerSiteKind.ProviderCall);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GuildA2AContentLengthComputationRequiresProviderContentProvenance(bool known)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "using var response = await A2AInput.Client.GetAsync(new Uri(\"https://peer.invalid\"), token); "
            + "_ = " + (known ? "response.Content.Headers" : "A2AInput.UnknownHeaders") + ".ContentLength; await Task.CompletedTask;",
            "static class A2AInput { internal static System.Net.Http.HttpClient Client = new(); internal static System.Net.Http.Headers.HttpContentHeaders UnknownHeaders = null!; }");

        Assert.Equal(!known, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.Headers.HttpContentHeaders.ContentLength"));

        Assert.Equal(known, result.Items.Any(static site => site.Callee == "System.Net.Http.Headers.HttpContentHeaders.ContentLength"
            && site.Kind == HostedProducerSiteKind.ProviderCall));
    }

    [Theory]
    [InlineData("response")]
    [InlineData("stream")]
    [InlineData("mixed-stream")]
    [InlineData("replaced-content")]
    [InlineData("aliased-content")]
    [InlineData("escaped-response")]
    public void GuildA2AUnknownOrMixedHttpResultsKeepTheirCleanupObligation(string resource)
    {
        string body = resource switch
        {
            "response" => "using var response = A2AInput.UnknownResponse;",
            "stream" => "await using var stream = await A2AInput.UnknownResponse.Content.ReadAsStreamAsync(token);",
            "replaced-content" => "using var response = await A2AInput.Client.GetAsync(new Uri(\"https://peer.invalid\"), token); "
                + "response.Content = A2AInput.UnknownResponse.Content; await using var stream = await response.Content.ReadAsStreamAsync(token);",
            "aliased-content" => "using var response = await A2AInput.Client.GetAsync(new Uri(\"https://peer.invalid\"), token); "
                + "var alias = response; alias.Content = A2AInput.UnknownResponse.Content; await using var stream = await response.Content.ReadAsStreamAsync(token);",
            "escaped-response" => "using var response = await A2AInput.Client.GetAsync(new Uri(\"https://peer.invalid\"), token); "
                + "A2AInput.Replace(response); await using var stream = await response.Content.ReadAsStreamAsync(token);",
            _ => "using var response = await A2AInput.Client.GetAsync(new Uri(\"https://peer.invalid\"), token); "
                + "await using var stream = DateTime.UtcNow.Ticks > 0 ? await response.Content.ReadAsStreamAsync(token) : A2AInput.UnknownStream;",
        };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(body + "await Task.CompletedTask;",
            "static class A2AInput { internal static System.Net.Http.HttpClient Client = new(); "
            + "internal static System.Net.Http.HttpResponseMessage UnknownResponse = null!; internal static System.IO.Stream UnknownStream = null!; "
            + "internal static void Replace(System.Net.Http.HttpResponseMessage response) { response.Content = UnknownResponse.Content; } }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED");
    }

    [Fact]
    public void GuildA2AExactBclHeaderStorageAndJsonScalarReadsHaveNoOpaqueCalls()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "var client = new System.Net.Http.HttpClient(); client.MaxResponseContentBufferSize = 4096; "
            + "client.DefaultRequestHeaders.Remove(\"X-Fixture\"); client.DefaultRequestHeaders.TryAddWithoutValidation(\"X-Fixture\", \"value\"); "
            + "var element = default(System.Text.Json.JsonElement); element.TryGetInt64(out _); element.TryGetDecimal(out _); "
            + "var spin = new System.Threading.SpinWait(); spin.SpinOnce(); await Task.CompletedTask;");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && (diagnostic.Detail.StartsWith("System.Net.Http.", StringComparison.Ordinal)
                || diagnostic.Detail.StartsWith("System.Text.Json.JsonElement.", StringComparison.Ordinal)
                || diagnostic.Detail == "System.Threading.SpinWait.SpinOnce"));
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverGuildA2A(string body, string extra = "") =>
        DiscoverGuildA2ACompilation(CompileWithProductionReferencePack(R2Source(R2Admission + body, extra), "RetroDownfall.Arcanum.Api"));

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverGuildA2ACompilation(CSharpCompilation compilation)
    {
        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }
}
