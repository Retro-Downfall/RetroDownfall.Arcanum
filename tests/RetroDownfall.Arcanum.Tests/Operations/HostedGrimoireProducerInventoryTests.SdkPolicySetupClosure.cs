using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("timeout-short", true)]
    [InlineData("timeout-qualified", true)]
    [InlineData("timeout-alias", true)]
    [InlineData("timeout-finite", false)]
    [InlineData("timeout-default", false)]
    [InlineData("timeout-dynamic", false)]
    [InlineData("timeout-getter", false)]
    [InlineData("timeout-spoof", false)]
    [InlineData("timeout-shadow", false)]
    [InlineData("timeout-conversion", false)]
    [InlineData("timeout-mutation", false)]
    [InlineData("timeout-escape", false)]
    [InlineData("policy", true)]
    [InlineData("alias", true)]
    [InlineData("constructor-effect", true)]
    [InlineData("work-only", true)]
    [InlineData("work-only-spaced", true)]
    [InlineData("compact-group", true)]
    [InlineData("after-scope", true)]
    [InlineData("released", true)]
    [InlineData("unknown", false)]
    [InlineData("mixed", false)]
    [InlineData("derived", false)]
    [InlineData("mutation", false)]
    [InlineData("escape", false)]
    [InlineData("policy-captures-options", false)]
    [InlineData("logging", false)]
    public void HostedContinuationSdkPolicyRegistrationStoresWithoutExecutingSendCallbacks(string shape, bool closed)
    {
        string creation = "new OpenAI.OpenAIClientOptions { Endpoint = new Uri(\"https://fixture.invalid\"), RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0), Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http) }";

        string? timeout = shape switch
        {
            "timeout-short" => "Timeout.InfiniteTimeSpan",
            "timeout-qualified" => "global::System.Threading.Timeout.InfiniteTimeSpan",
            "timeout-alias" => "TimeoutAlias.InfiniteTimeSpan",
            "timeout-finite" => "TimeSpan.FromSeconds(100)",
            "timeout-default" => "default",
            "timeout-dynamic" => "SdkSetupInput.TimeoutValue()",
            "timeout-getter" => "SdkSetupInput.TimeoutProperty",
            "timeout-spoof" => "SdkTimeoutSpoof.InfiniteTimeSpan",
            "timeout-shadow" => "System.Threading.Timeout.InfiniteTimeSpan",
            "timeout-conversion" => "new SdkTimeoutAmount()",
            "timeout-mutation" or "timeout-escape" => "System.Threading.Timeout.InfiniteTimeSpan",
            _ => null,
        };

        if (timeout is not null) creation = creation.Replace("Endpoint =", "NetworkTimeout = " + timeout + ", Endpoint =", StringComparison.Ordinal);

        string initializer = shape switch
        {
            "unknown" => "SdkSetupInput.Options()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? " + creation + " : SdkSetupInput.Options()",
            "derived" => creation.Replace("new OpenAI.OpenAIClientOptions", "new DerivedSdkSetupOptions", StringComparison.Ordinal),
            "logging" => creation.Replace("Endpoint =", "ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions(), Endpoint =", StringComparison.Ordinal),
            _ => creation,
        };

        string policyReceiver = shape == "alias" ? "alias" : "options";

        string setup = "using System.Net.Http.HttpClient http = new(new System.Net.Http.SocketsHttpHandler()); var options = " + initializer + "; "
            + (shape == "alias" ? "var alias = options; " : "")
            + policyReceiver + ".AddPolicy(new SdkSetupPolicy(" + (shape == "policy-captures-options" ? "options" : "null") + "), System.ClientModel.Primitives.PipelinePosition.PerCall); "
            + (shape is "mutation" or "timeout-mutation" ? "options.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(1); " : "")
            + (shape is "escape" or "timeout-escape" ? "SdkSetupInput.Escape(options); " : "")
            + "_ = new OpenAI.Chat.ChatClient(\"fixture-model\", new System.ClientModel.ApiKeyCredential(\"fixture-key\"), options); System.IO.File.Exists(\"sdk-policy-control\");";

        string constructor = shape is "constructor-effect" or "work-only" or "work-only-spaced" or "compact-group" or "after-scope" or "released" ? "System.IO.File.Delete(\"policy-construction-effect\");" : "System.IO.File.Exists(\"policy-construction-read\");";

        string helper = "internal static class SdkSetupProbe { internal static void Run() { " + setup + " } } "
            + "internal static class SdkSetupInput { internal static OpenAI.OpenAIClientOptions? Saved; internal static extern OpenAI.OpenAIClientOptions Options(); internal static extern void Escape(OpenAI.OpenAIClientOptions options); "
            + "internal static extern TimeSpan TimeoutValue(); internal static TimeSpan TimeoutProperty { get { System.IO.File.Delete(\"authored-timeout-effect\"); return TimeSpan.FromMilliseconds(-1); } } } "
            + "internal static class SdkTimeoutSpoof { internal static TimeSpan InfiniteTimeSpan { get { System.IO.File.Delete(\"authored-timeout-effect\"); return TimeSpan.FromMilliseconds(-1); } } } "
            + "internal readonly struct SdkTimeoutAmount { public static implicit operator TimeSpan(SdkTimeoutAmount value) { System.IO.File.Delete(\"authored-timeout-effect\"); return TimeSpan.FromMilliseconds(-1); } } "
            + "internal sealed class DerivedSdkSetupOptions : OpenAI.OpenAIClientOptions { public override void Freeze() { System.IO.File.Delete(\"derived-options-freeze\"); base.Freeze(); } } "
            + "internal sealed class SdkSetupPolicy : System.ClientModel.Primitives.PipelinePolicy { internal SdkSetupPolicy(OpenAI.OpenAIClientOptions? options) { SdkSetupInput.Saved = options; " + constructor + " } "
            + "public override void Process(System.ClientModel.Primitives.PipelineMessage message, System.Collections.Generic.IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int index) { System.IO.File.Delete(\"send-only-sync\"); } "
            + "public override ValueTask ProcessAsync(System.ClientModel.Primitives.PipelineMessage message, System.Collections.Generic.IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int index) { System.IO.File.Delete(\"send-only-async\"); return ValueTask.CompletedTask; } }";

        if (shape == "timeout-shadow") helper += " namespace System.Threading { internal static class Timeout { internal static TimeSpan InfiniteTimeSpan { get { System.IO.File.Delete(\"authored-timeout-effect\"); return TimeSpan.FromMilliseconds(-1); } } } }";

        string admission = shape switch
        {
            "work-only" => AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal),
            "work-only-spaced" => AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " ",
            "compact-group" => R2Admission.TrimEnd(),
            "after-scope" => "{ " + R2Admission + " } ",
            "released" => R2Admission + "held.Dispose(); lease.Dispose(); ",
            _ => R2Admission,
        };

        string aliases = shape == "timeout-alias" ? "using TimeoutAlias = System.Threading.Timeout; " : "";

        CSharpCompilation compilation = CompileWithProductionReferencePack(aliases + R2Source(admission + "SdkSetupProbe.Run(); await Task.CompletedTask;", helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists" && site.EnclosingType == "SdkSetupProbe");

        Assert.Equal(!closed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "OpenAI.Chat.ChatClient..ctor"));

        Assert.DoesNotContain(result.Items, static site => site.EnclosingType == "SdkSetupPolicy" && site.Member is "Process" or "ProcessAsync");

        if (shape is "timeout-getter" or "timeout-spoof" or "timeout-shadow" or "timeout-conversion")
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete"
                && site.EnclosingType is "SdkSetupInput" or "SdkTimeoutSpoof" or "System.Threading.Timeout" or "SdkTimeoutAmount");
        }

        if (closed) Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail is "System.ClientModel.Primitives.ClientPipelineOptions.AddPolicy" or "System.ClientModel.Primitives.PipelinePolicy..ctor");

        if (shape is "constructor-effect" or "work-only" or "work-only-spaced" or "compact-group" or "after-scope" or "released")
        {
            HostedProducerSite effect = Assert.Single(result.Items, static site => site.EnclosingType == "SdkSetupPolicy" && site.Callee == "System.IO.File.Delete");

            Assert.Equal(shape is not ("after-scope" or "released"), effect.WorkFrontierCapsuleId is not null);

            Assert.Equal(shape is not ("work-only" or "work-only-spaced" or "after-scope" or "released"), effect.EffectFrontierCapsuleId is not null);

            if (shape is "work-only" or "work-only-spaced" or "after-scope" or "released") Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete");
        }
    }

    [Theory]
    [InlineData("infinite", true)]
    [InlineData("legacy-three", true)]
    [InlineData("missing-endpoint", false)]
    [InlineData("missing-transport", false)]
    [InlineData("missing-retry", false)]
    [InlineData("finite", false)]
    [InlineData("default", false)]
    [InlineData("retry-nonzero", false)]
    [InlineData("mutated", false)]
    [InlineData("escaped", false)]
    [InlineData("extra", false)]
    public void SdkEmbeddingInfinityPreservesTheExactClosedOptionsCore(string shape, bool closed)
    {
        string initializer = "Endpoint = new Uri(\"https://fixture.invalid\"), "
            + "Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http), "
            + "RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0), "
            + "NetworkTimeout = System.Threading.Timeout.InfiniteTimeSpan";

        initializer = shape switch
        {
            "legacy-three" => initializer.Replace(", NetworkTimeout = System.Threading.Timeout.InfiniteTimeSpan", "", StringComparison.Ordinal),
            "missing-endpoint" => initializer.Replace("Endpoint = new Uri(\"https://fixture.invalid\"), ", "", StringComparison.Ordinal),
            "missing-transport" => initializer.Replace("Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http), ", "", StringComparison.Ordinal),
            "missing-retry" => initializer.Replace("RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0), ", "", StringComparison.Ordinal),
            "finite" => initializer.Replace("System.Threading.Timeout.InfiniteTimeSpan", "TimeSpan.FromSeconds(100)", StringComparison.Ordinal),
            "default" => initializer.Replace("System.Threading.Timeout.InfiniteTimeSpan", "default", StringComparison.Ordinal),
            "retry-nonzero" => initializer.Replace("ClientRetryPolicy(0)", "ClientRetryPolicy(1)", StringComparison.Ordinal),
            "extra" => initializer + ", ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions()",
            _ => initializer,
        };

        string body = R2Admission + "using System.Net.Http.HttpClient http = new(new System.Net.Http.SocketsHttpHandler()); "
            + "var options = new OpenAI.OpenAIClientOptions { " + initializer + " }; "
            + (shape == "mutated" ? "options.NetworkTimeout = TimeSpan.FromSeconds(5); " : "")
            + (shape == "escaped" ? "SdkTimeoutCapture.Take(options); " : "")
            + "_ = new OpenAI.Embeddings.EmbeddingClient(\"model\", new System.ClientModel.ApiKeyCredential(\"synthetic\"), options); "
            + "System.IO.File.Exists(\"timeout-construction-control\"); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body,
            "internal static class SdkTimeoutCapture { internal static extern void Take(OpenAI.OpenAIClientOptions options); }"), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(!closed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "OpenAI.Embeddings.EmbeddingClient..ctor"));

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Theory]
    [InlineData("UntrustedSdkTimeout")]
    [InlineData("System.Private.CoreLib")]
    [InlineData("System.Runtime")]
    public void SdkInfiniteTimeoutRejectsAnUnsignedMetadataFieldWithTheFrameworkName(string assemblyName)
    {
        CSharpCompilation foreign = CompileWithProductionReferencePack("""
            namespace System.Threading
            {
                public static class Timeout
                {
                    public static readonly System.TimeSpan InfiniteTimeSpan = Effect();
                    private static System.TimeSpan Effect()
                    {
                        System.IO.File.Delete("NEVER-EXECUTED-TIMEOUT-COUNTERFEIT");
                        return System.TimeSpan.FromMilliseconds(-1);
                    }
                }
            }
            """, "RetroDownfall.Arcanum.Api").WithAssemblyName(assemblyName);

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        PortableExecutableReference reference = MetadataReference.CreateFromImage(bytes.ToArray(),
            new MetadataReferenceProperties(aliases: ["counterTimeout"]));

        string body = R2Admission + "using System.Net.Http.HttpClient http = new(new System.Net.Http.SocketsHttpHandler()); "
            + "var options = new OpenAI.OpenAIClientOptions { Endpoint = new Uri(\"https://fixture.invalid\"), "
            + "Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http), "
            + "RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0), "
            + "NetworkTimeout = counterTimeout::System.Threading.Timeout.InfiniteTimeSpan }; "
            + "_ = new OpenAI.Embeddings.EmbeddingClient(\"model\", new System.ClientModel.ApiKeyCredential(\"synthetic\"), options); "
            + "System.IO.File.Exists(\"foreign-timeout-control\"); await Task.CompletedTask;";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            "extern alias counterTimeout; " + R2Source(body), "RetroDownfall.Arcanum.Api").AddReferences(reference);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "OpenAI.Embeddings.EmbeddingClient..ctor");

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedContinuationActualProviderFactoriesRetainClosedSdkPolicySetup()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProductionSites(
            HostedProducerProductionPartition.Primary, ["Loremaster"]);

        Assert.NotEmpty(result.Items);

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail is "System.ClientModel.Primitives.ClientPipelineOptions.AddPolicy"
                    or "System.ClientModel.Primitives.PipelinePolicy..ctor"
                    or "OpenAI.Chat.ChatClient..ctor")];

        Assert.True(failures.Length == 0, HostedGrimoireProducerInventory.RenderDiagnosticReport(failures));

        Assert.Contains(result.Items, static site => site.Kind == HostedProducerSiteKind.ProviderCall);
    }

    [Fact]
    public void HostedContinuationPinnedSdkSetupHasOnlyStoredPoliciesAndNoPhysicalSend()
    {
        var policy = new SetupOnlyObservedPolicy();

        using var handler = new SetupOnlyNoSendHandler();

        using var http = new System.Net.Http.HttpClient(handler);

        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = new Uri("https://fixture.invalid"),
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(0),
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http),
        };

        options.AddPolicy(policy, System.ClientModel.Primitives.PipelinePosition.PerCall);

        _ = new OpenAI.Chat.ChatClient("fixture-model", new System.ClientModel.ApiKeyCredential("fixture-key"), options);

        Assert.Equal(0, policy.ProcessCalls);

        Assert.Equal(0, handler.SendCalls);

        Assert.Throws<InvalidOperationException>(() => options.AddPolicy(policy, System.ClientModel.Primitives.PipelinePosition.PerTry));

        System.Reflection.ConstructorInfo constructor = Assert.Single(typeof(System.ClientModel.Primitives.PipelinePolicy)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic), static candidate => candidate.GetParameters().Length == 0);

        byte[] body = Assert.IsType<byte[]>(constructor.GetMethodBody()?.GetILAsByteArray());

        // The pinned protected base constructor only calls Object..ctor and returns.
        Assert.Equal(7, body.Length);

        Assert.Equal(new byte[] { 0x02, 0x28 }, body[..2]);

        Assert.Equal(0x2a, body[6]);

        System.Reflection.ConstructorInfo called = Assert.IsAssignableFrom<System.Reflection.ConstructorInfo>(constructor.Module.ResolveMethod(BitConverter.ToInt32(body, 2)));

        Assert.Equal(typeof(object), called.DeclaringType);

        Assert.Empty(called.GetParameters());
    }

    private sealed class SetupOnlyObservedPolicy : System.ClientModel.Primitives.PipelinePolicy
    {
        internal int ProcessCalls { get; private set; }

        public override void Process(System.ClientModel.Primitives.PipelineMessage message, IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int currentIndex) => ProcessCalls++;

        public override ValueTask ProcessAsync(System.ClientModel.Primitives.PipelineMessage message, IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int currentIndex)
        {
            ProcessCalls++;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class SetupOnlyNoSendHandler : System.Net.Http.HttpMessageHandler
    {
        internal int SendCalls { get; private set; }

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCalls++;

            throw new InvalidOperationException("SDK setup attempted a physical request.");
        }
    }
}
