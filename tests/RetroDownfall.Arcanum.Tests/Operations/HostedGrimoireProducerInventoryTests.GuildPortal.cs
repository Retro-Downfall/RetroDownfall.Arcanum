using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("joined", true)]
    [InlineData("missing-finally", false)]
    [InlineData("overwrite-unobserved", false)]
    [InlineData("escaped", false)]
    [InlineData("guarded-after-release", false)]
    public void GuildPortalPulseRetainsItsExactTaskUntilCompletion(string shape, bool joined)
    {
        const string observe = "if (pending is not null) { await pending.ConfigureAwait(false); }";

        string loop = "while (!token.IsCancellationRequested) { if (pending is null || pending.IsCompleted) { "
            + observe + " pending = PortalPulse.DrainAsync(); } }";

        if (shape == "overwrite-unobserved") loop = loop.Replace(observe, "", StringComparison.Ordinal);

        if (shape == "escaped") loop = loop.Replace("pending = PortalPulse.DrainAsync();", "pending = PortalPulse.DrainAsync(); PortalPulse.Escaped = pending;", StringComparison.Ordinal);

        string finallyBody = shape switch
        {
            "missing-finally" => "",
            "guarded-after-release" => "held.Dispose(); " + observe,
            _ => observe,
        };

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "Task? pending = null; try { " + loop + " } finally { " + finallyBody + " }",
            "static class PortalPulse { internal static Task? Escaped; internal static async Task DrainAsync() { await Task.Yield(); System.IO.File.Delete(\"portal-pulse\"); } }");

        Assert.Contains(result.Items, static site => site.EnclosingType == "PortalPulse" && site.Callee == "System.IO.File.Delete");

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith("PortalPulse.DrainAsync", StringComparison.Ordinal)));

        Assert.Equal(joined, !result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete"));
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("forwarded-acquisition", false)]
    [InlineData("released", false)]
    [InlineData("detached", false)]
    public void GuildPortalDiscoveryCallbackRequiresItsOwnHeldEffectGroup(string shape, bool admitted)
    {
        const string acquisition = "DiscoveryFixture.Gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out var lease)";

        const string callback = "return await send(token).ConfigureAwait(false);";

        string helper = "namespace PortalDiscovery { using RetroDownfall.Arcanum.Infrastructure.Data; "
            + "internal static class DiscoveryFixture { internal static IGrimoireConnectionAdmissionGate Gate = null!; "
            + "private static bool TryAcquire(out IGrimoireWorkLease lease) => Gate.TryAcquireWorkLease(GrimoireWorkKind.WorkspaceIndexing, out lease); } "
            + "internal static class Adapter { internal static async Task<int> AdmitAsync(Func<CancellationToken, Task<int>> send, CancellationToken token) { "
            + "if (!" + acquisition + " || lease is null) throw new InvalidOperationException(); "
            + "await using IGrimoireWorkLease work = lease; "
            + "if (!work.TryBeginExternalEffectGroup(out var effect) || effect is null) throw new InvalidOperationException(); "
            + "using IDisposable held = effect; " + callback + " } } }";

        if (shape == "forwarded-acquisition") helper = helper.Replace(acquisition, "DiscoveryFixture.TryAcquire(out var lease)", StringComparison.Ordinal)
            .Replace("private static bool TryAcquire", "internal static bool TryAcquire", StringComparison.Ordinal);

        if (shape == "released") helper = helper.Replace(callback, "held.Dispose(); " + callback, StringComparison.Ordinal);

        if (shape == "detached") helper = helper.Replace(callback, "_ = send(token); await Task.Yield(); return 0;", StringComparison.Ordinal);

        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(
            "_ = await PortalDiscovery.Adapter.AdmitAsync(ct => { System.IO.File.Delete(\"terminal-discovery\"); return Task.FromResult(1); }, token);", helper),
            "RetroDownfall.Arcanum.Api");

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2ACompilation(compilation);

        HostedProducerSite transport = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(admitted, transport.EffectFrontierCapsuleId is not null);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("replaced-content", false)]
    [InlineData("borrowed-response", false)]
    [InlineData("other-post-overload", false)]
    public void GuildPortalHttpResponseOperationsRequireAnExactOwnedPost(string shape, bool owned)
    {
        const string post = "await PortalHttp.Client.PostAsync(new Uri(\"https://peer.invalid/\"), content, token).ConfigureAwait(false)";

        string origin = shape switch
        {
            "borrowed-response" => "PortalHttp.Borrowed",
            "other-post-overload" => post.Replace("new Uri(\"https://peer.invalid/\")", "\"https://peer.invalid/\"", StringComparison.Ordinal),
            _ => post,
        };

        string replace = shape == "replaced-content" ? "response.Content = PortalHttp.Content;" : "";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "using var content = new System.Net.Http.ByteArrayContent(new byte[] { 123, 125 }); "
            + "content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(\"application/json\"); "
            + "using System.Net.Http.HttpResponseMessage response = " + origin + "; " + replace
            + "_ = response.Content.Headers.ContentLength; await using var stream = await response.Content.ReadAsStreamAsync(token);",
            "static class PortalHttp { internal static System.Net.Http.HttpClient Client = null!; internal static System.Net.Http.HttpResponseMessage Borrowed = null!; internal static System.Net.Http.HttpContent Content = null!; }");

        if (shape == "exact")
        {
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail is "System.Net.Http.ByteArrayContent..ctor" or "System.Net.Http.Headers.HttpContentHeaders.ContentType setter"
                    or "System.Net.Http.Headers.MediaTypeHeaderValue..ctor");
        }

        foreach (string callee in new[] { "System.Net.Http.Headers.HttpContentHeaders.ContentLength", "System.Net.Http.HttpContent.ReadAsStreamAsync" })
        {
            Assert.Equal(owned, result.Items.Any(site => site.Callee == callee && site.Kind == HostedProducerSiteKind.ProviderCall));

            Assert.Equal(!owned, result.Diagnostics.Any(diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == callee));
        }
    }

    [Fact]
    public void GuildPortalCapabilityReadsExactPinnedSdkStorage()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "_ = System.Linq.Enumerable.FirstOrDefault(PortalInput.Card.Capabilities!.Extensions!)?.Uri; await Task.CompletedTask;",
            "static class PortalInput { internal static A2A.AgentCard Card = null!; }");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail is "A2A.AgentCapabilities.Extensions" or "A2A.AgentExtension.Uri");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("factory-replacement", false)]
    [InlineData("client-replacement", false)]
    public void GuildPortalNamedClientCleanupRequiresExactFactoryComposition(string mutation, bool owned)
    {
        const string consumer = "internal void Use() { using System.Net.Http.HttpClient client = CreateNamed(out bool negotiated); } "
            + "private System.Net.Http.HttpClient CreateNamed(out bool negotiated) { negotiated = true; "
            + "var selected = _factory.CreateClient(\"A2AOutbound\"); selected.MaxResponseContentBufferSize = 4096; return selected; }";

        string methods = mutation == "client-replacement"
            ? consumer.Replace("return selected;", "selected = FactoryInput.Http; return selected;", StringComparison.Ordinal)
            : consumer;

        var result = DiscoverGuildA2ACompilation(CompileNamedFactory(mutation, methods));

        Assert.True(owned == result.Items.Any(static site => site.Callee == "System.Net.Http.HttpClient.Dispose"
            && site.Kind == HostedProducerSiteKind.ProviderCall && site.EffectFrontierCapsuleId is not null),
            HostedGrimoireProducerInventory.RenderDiagnosticReport(result.Diagnostics));

        Assert.Equal(!owned, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.Net.Http.HttpClient.Dispose;", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("new System.Net.Http.ByteArrayContent(new byte[] { 123, 125 })", true)]
    [InlineData("PortalContent.Borrowed", false)]
    public void GuildPortalByteContentCleanupRequiresAnExactAllocation(string origin, bool owned)
    {
        var result = DiscoverGuildA2A("using System.Net.Http.ByteArrayContent content = " + origin + "; await Task.CompletedTask;",
            "static class PortalContent { internal static System.Net.Http.ByteArrayContent Borrowed = null!; }");

        Assert.Equal(owned, result.Items.Any(static site => site.Callee == "System.Net.Http.ByteArrayContent.Dispose"
            && site.Kind == HostedProducerSiteKind.ProviderCall && site.EffectFrontierCapsuleId is not null));

        Assert.Equal(!owned, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.Net.Http.ByteArrayContent.Dispose;", StringComparison.Ordinal)));
    }
}
