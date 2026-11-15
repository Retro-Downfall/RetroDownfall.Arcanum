using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("status", true)]
    [InlineData("success", true)]
    [InlineData("both-and-stream", true)]
    [InlineData("forwarded", true)]
    [InlineData("alternate-exact-client", true)]
    [InlineData("effectful-forwarded", true)]
    [InlineData("unknown-return", false)]
    [InlineData("new-return", false)]
    [InlineData("unknown-response", false)]
    [InlineData("reassigned-response", false)]
    [InlineData("replaced-content", false)]
    [InlineData("aliased-content", false)]
    [InlineData("escaped-response", false)]
    [InlineData("unknown-stream", false)]
    [InlineData("mixed-stream", false)]
    [InlineData("status-write", false)]
    [InlineData("status-compound-write", false)]
    [InlineData("status-deconstruction-write", false)]
    public void Http_response_status_reads_are_scalar_but_cleanup_retains_exact_provider_origin(string shape, bool known)
    {
        string factory = shape switch
        {
            "forwarded" => "HttpStatusInput.SendAsync(HttpStatusInput.Client, token)",
            "effectful-forwarded" => "HttpStatusInput.EffectfulSendAsync(HttpStatusInput.Client, token)",
            // Different exact send is still a provider result, not an endpoint identity proof.
            "alternate-exact-client" => "HttpStatusInput.SendOtherAsync(HttpStatusInput.Client, token)",
            "unknown-return" => "HttpStatusInput.UnknownReturnAsync(HttpStatusInput.Client, token)",
            "new-return" => "HttpStatusInput.NewResponseAsync(HttpStatusInput.Client, token)",
            _ => "HttpStatusInput.Client.GetAsync(new Uri(\"https://peer.invalid\"), token)",
        };

        string declaration = shape == "unknown-response"
            ? "using var response = HttpStatusInput.UnknownResponse; "
            : "using var response = await " + factory + "; ";

        string mutation = shape switch
        {
            "replaced-content" => "response.Content = HttpStatusInput.UnknownResponse.Content; ",
            "aliased-content" => "var alias = response; alias.Content = HttpStatusInput.UnknownResponse.Content; ",
            "escaped-response" => "HttpStatusInput.Replace(response); ",
            "status-write" => "response.StatusCode = System.Net.HttpStatusCode.OK; ",
            "status-compound-write" => "response.StatusCode |= System.Net.HttpStatusCode.OK; ",
            "status-deconstruction-write" => "(response.StatusCode, _) = (System.Net.HttpStatusCode.OK, 0); ",
            _ => string.Empty,
        };

        string reads = shape switch
        {
            "status" => "_ = response.StatusCode; ",
            "success" => "_ = response.IsSuccessStatusCode; ",
            _ => "_ = response.StatusCode; _ = response.IsSuccessStatusCode; ",
        };

        string stream = shape switch
        {
            "both-and-stream" => "await using var stream = await response.Content.ReadAsStreamAsync(token); _ = await stream.ReadAsync(new byte[8], token); ",
            "unknown-stream" => "await using var stream = HttpStatusInput.UnknownStream; ",
            "mixed-stream" => "await using var stream = DateTime.UtcNow.Ticks > 0 ? await response.Content.ReadAsStreamAsync(token) : HttpStatusInput.UnknownStream; ",
            _ => string.Empty,
        };

        string body = declaration + mutation + reads + stream + "await Task.CompletedTask;";

        if (shape == "reassigned-response")
            body = "var response = await " + factory + "; response = HttpStatusInput.UnknownResponse; "
                + "using (response) { " + reads + " } await Task.CompletedTask;";

        const string helpers = "static class HttpStatusInput { "
            + "internal static System.Net.Http.HttpClient Client = new(); "
            + "internal static System.Net.Http.HttpClient OtherClient = new(); "
            + "internal static System.Net.Http.HttpResponseMessage UnknownResponse = null!; "
            + "internal static System.IO.Stream UnknownStream = null!; "
            + "internal static async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpClient client, CancellationToken token) "
            + "=> await client.GetAsync(new Uri(\"https://peer.invalid\"), token).ConfigureAwait(false); "
            + "internal static async Task<System.Net.Http.HttpResponseMessage> SendOtherAsync(System.Net.Http.HttpClient ignored, CancellationToken token) "
            + "=> await OtherClient.GetAsync(new Uri(\"https://other.invalid\"), token).ConfigureAwait(false); "
            + "internal static async Task<System.Net.Http.HttpResponseMessage> EffectfulSendAsync(System.Net.Http.HttpClient client, CancellationToken token) "
            + "{ System.IO.File.Delete(\"http-forward-effect\"); return await client.GetAsync(new Uri(\"https://peer.invalid\"), token).ConfigureAwait(false); } "
            + "internal static Task<System.Net.Http.HttpResponseMessage> NewResponseAsync(System.Net.Http.HttpClient ignored, CancellationToken token) "
            + "=> Task.FromResult(new System.Net.Http.HttpResponseMessage()); "
            + "internal static Task<System.Net.Http.HttpResponseMessage> UnknownReturnAsync(System.Net.Http.HttpClient ignored, CancellationToken token) "
            + "=> Task.FromResult(UnknownResponse); "
            + "internal static void Replace(System.Net.Http.HttpResponseMessage value) { value.Content = UnknownResponse.Content; } }";

        CSharpCompilation compilation = CompileWithProductionReferencePack(
            R2Source(R2Admission + body, helpers), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        bool unresolved = result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && (diagnostic.Detail.StartsWith("System.Net.Http.HttpResponseMessage.Dispose", StringComparison.Ordinal)
                || diagnostic.Detail.StartsWith("System.IO.Stream.DisposeAsync", StringComparison.Ordinal)));

        Assert.Equal(!known, unresolved);

        // Pure exact framework reads require no result-origin proof, including on an
        // unknown response whose cleanup must still be refused independently.
        if (!shape.StartsWith("status-", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(result.Items, static site => site.Callee is
                "System.Net.Http.HttpResponseMessage.StatusCode" or "System.Net.Http.HttpResponseMessage.IsSuccessStatusCode");

            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail is "System.Net.Http.HttpResponseMessage.StatusCode" or "System.Net.Http.HttpResponseMessage.IsSuccessStatusCode");
        }

        if (!known) return;

        Assert.Contains(result.Items, static site => site.Callee == "System.Net.Http.HttpResponseMessage.Dispose"
            && site.Kind == HostedProducerSiteKind.ProviderCall);

        if (shape == "effectful-forwarded")
        {
            HostedProducerSite effect = Assert.Single(EnumerationDeleteSites(result));

            Assert.NotNull(effect.WorkFrontierCapsuleId);

            Assert.NotNull(effect.EffectFrontierCapsuleId);
        }

        if (shape == "both-and-stream")
        {
            Assert.Contains(result.Items, static site => site.Callee == "System.IO.Stream.DisposeAsync"
                && site.Kind == HostedProducerSiteKind.ProviderCall);

            Assert.Contains(result.Items, static site => site.Callee == "System.IO.Stream.ReadAsync"
                && site.Kind == HostedProducerSiteKind.ProviderCall);
        }
    }

    [Fact]
    public void Http_status_lookalike_getter_retains_its_authored_effect()
    {
        string source = R2Source(R2Admission
            + "var response = new HttpStatusLookalike(); _ = response.StatusCode; await Task.CompletedTask;",
            "sealed class HttpStatusLookalike : System.Net.Http.HttpResponseMessage { "
            + "public new System.Net.HttpStatusCode StatusCode { get { System.IO.File.Delete(\"status-getter-effect\"); return System.Net.HttpStatusCode.OK; } } }");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = EnumerationDiscover(compilation);

        HostedProducerSite effect = Assert.Single(EnumerationDeleteSites(result));

        Assert.NotNull(effect.WorkFrontierCapsuleId);

        Assert.NotNull(effect.EffectFrontierCapsuleId);
    }
}
