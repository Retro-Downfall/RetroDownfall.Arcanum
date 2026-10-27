using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
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
            + (shape == "mutation" ? "options.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(1); " : "")
            + (shape == "escape" ? "SdkSetupInput.Escape(options); " : "")
            + "_ = new OpenAI.Chat.ChatClient(\"fixture-model\", new System.ClientModel.ApiKeyCredential(\"fixture-key\"), options); System.IO.File.Exists(\"sdk-policy-control\");";

        string constructor = shape is "constructor-effect" or "work-only" or "work-only-spaced" or "compact-group" or "after-scope" or "released" ? "System.IO.File.Delete(\"policy-construction-effect\");" : "System.IO.File.Exists(\"policy-construction-read\");";

        string helper = "internal static class SdkSetupProbe { internal static void Run() { " + setup + " } } "
            + "internal static class SdkSetupInput { internal static OpenAI.OpenAIClientOptions? Saved; internal static extern OpenAI.OpenAIClientOptions Options(); internal static extern void Escape(OpenAI.OpenAIClientOptions options); } "
            + "internal sealed class DerivedSdkSetupOptions : OpenAI.OpenAIClientOptions { public override void Freeze() { System.IO.File.Delete(\"derived-options-freeze\"); base.Freeze(); } } "
            + "internal sealed class SdkSetupPolicy : System.ClientModel.Primitives.PipelinePolicy { internal SdkSetupPolicy(OpenAI.OpenAIClientOptions? options) { SdkSetupInput.Saved = options; " + constructor + " } "
            + "public override void Process(System.ClientModel.Primitives.PipelineMessage message, System.Collections.Generic.IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int index) { System.IO.File.Delete(\"send-only-sync\"); } "
            + "public override ValueTask ProcessAsync(System.ClientModel.Primitives.PipelineMessage message, System.Collections.Generic.IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline, int index) { System.IO.File.Delete(\"send-only-async\"); return ValueTask.CompletedTask; } }";

        string admission = shape switch
        {
            "work-only" => AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal),
            "work-only-spaced" => AcquireWork.Replace("return Task.CompletedTask;", "return;", StringComparison.Ordinal) + " ",
            "compact-group" => R2Admission.TrimEnd(),
            "after-scope" => "{ " + R2Admission + " } ",
            "released" => R2Admission + "held.Dispose(); lease.Dispose(); ",
            _ => R2Admission,
        };

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(admission + "SdkSetupProbe.Run(); await Task.CompletedTask;", helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Exists" && site.EnclosingType == "SdkSetupProbe");

        Assert.Equal(!closed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == "OpenAI.Chat.ChatClient..ctor"));

        Assert.DoesNotContain(result.Items, static site => site.EnclosingType == "SdkSetupPolicy" && site.Member is "Process" or "ProcessAsync");

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
