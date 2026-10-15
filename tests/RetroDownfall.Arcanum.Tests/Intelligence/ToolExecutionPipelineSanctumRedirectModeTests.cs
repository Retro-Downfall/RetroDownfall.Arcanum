using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>Exercises native read_url redirects through the real pipeline, provider and Sanctum guard.</summary>
public sealed class ToolExecutionPipelineSanctumRedirectModeTests : IDisposable
{
    private const string InitialUrl = "https://allowed.test/start";

    private const string PublicTarget = "https://off-list.test/page";

    private const string PrivateTarget = "https://private.test/page";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-sanctum-redirect-mode-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AuditOnly_public_off_list_redirect_returns_second_hop_content_and_records_one_breach()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        LocalHttpWebProvider provider = CreateProvider(clients);

        ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(harness, provider, SanctumMode.AuditOnly);

        Assert.False(processed.Denied);

        using JsonDocument result = JsonDocument.Parse(processed.ResultText);

        // The current ambient ward records the violation but refuses the public hop in AuditOnly.
        // RED must be this observable result (error rather than ok), not fixture/setup failure.
        Assert.Equal("ok", result.RootElement.GetProperty("status").GetString());

        Assert.Equal(PublicTarget, result.RootElement.GetProperty("data").GetProperty("finalUrl").GetString());

        Assert.Contains("controlled second-hop page", result.RootElement.GetProperty("data").GetProperty("markdown").GetString(), StringComparison.Ordinal);

        Assert.Equal([InitialUrl, PublicTarget], http.Requests);

        AssertOneTargetBreach(harness, PublicTarget);
    }

    [Fact]
    public async Task Strict_public_off_list_redirect_is_refused_before_second_request_and_records_one_breach()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.Strict, ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        LocalHttpWebProvider provider = CreateProvider(clients);

        ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(harness, provider, SanctumMode.Strict);

        // The initial allowlisted URL enters the native tool; its redirect is the refused operation.
        Assert.False(processed.Denied);

        AssertReadRefused(processed.ResultText);

        Assert.Equal([InitialUrl], http.Requests);

        AssertOneTargetBreach(harness, PublicTarget);
    }

    [Fact]
    public async Task AuditOnly_private_IP_redirect_is_refused_by_baseline_outbound_policy_even_when_campaign_allows_target()
    {
        // Explicitly allow private.test in Sanctum. A refusal cannot be attributed to an
        // off-list campaign violation: the independent untrusted-URL address rule must stop it.
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, ["allowed.test", "private.test"]);

        using RedirectScriptHandler http = new(PrivateTarget);

        using SingleClientFactory clients = new(http);

        LocalHttpWebProvider provider = CreateProvider(clients);

        ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(harness, provider, SanctumMode.AuditOnly);

        Assert.False(processed.Denied);

        AssertReadRefused(processed.ResultText);

        Assert.Equal([InitialUrl], http.Requests);

        Assert.Empty(harness.Breaches.Records);
    }

    // The initial target and the legacy alias follow the rule the redirect hop does: a target outside the allowed domains is recorded
    // as a breach in both modes, refused before any request in Strict, and fetched in AuditOnly.
    [Theory]
    [InlineData("read_url", SanctumMode.Strict)]
    [InlineData("read_url", SanctumMode.AuditOnly)]
    [InlineData("browse_web", SanctumMode.Strict)]
    [InlineData("browse_web", SanctumMode.AuditOnly)]
    public async Task The_initial_target_outside_the_allowed_domains_is_recorded_in_both_modes_and_fetched_only_in_audit_only(string toolName, SanctumMode mode)
    {
        SanctumPipelineHarness harness = CreateHarness(mode, ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        AITool tool = CreateTool(toolName, clients);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(tool, toolName,
            new Dictionary<string, object?> { ["url"] = PublicTarget }, TurnFor(harness, mode));

        if (mode == SanctumMode.Strict)
        {
            Assert.True(processed.Denied);

            Assert.Contains("The Sanctum Guard has blocked this action: NetworkEgress", processed.ResultText, StringComparison.Ordinal);

            Assert.Empty(http.Requests);
        }
        else
        {
            Assert.False(processed.Denied);

            Assert.Contains("controlled second-hop page", processed.ResultText, StringComparison.Ordinal);

            Assert.Equal([PublicTarget], http.Requests);
        }

        AssertOneTargetBreach(harness, PublicTarget, toolName);
    }

    [Theory]
    [InlineData(SanctumMode.AuditOnly)]
    [InlineData(SanctumMode.Strict)]
    public async Task The_initial_target_inside_the_allowed_domains_is_never_a_breach_in_either_mode(SanctumMode mode)
    {
        SanctumPipelineHarness harness = CreateHarness(mode, ["allowed.test", "off-list.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        // Both hosts are allowed, so the redirect is the same operation as the initial target: nothing is recorded or refused.
        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool("browse_web", clients), "browse_web",
            new Dictionary<string, object?> { ["url"] = InitialUrl }, TurnFor(harness, mode));

        Assert.False(processed.Denied);

        Assert.Contains("controlled second-hop page", processed.ResultText, StringComparison.Ordinal);

        Assert.Equal([InitialUrl, PublicTarget], http.Requests);

        Assert.Empty(harness.Breaches.Records);
    }

    // The legacy alias publishes the same ward as read_url, so its redirect hop is recorded in both modes and refused only in Strict.
    [Theory]
    [InlineData(SanctumMode.AuditOnly)]
    [InlineData(SanctumMode.Strict)]
    public async Task The_legacy_alias_redirect_outside_the_allowed_domains_is_recorded_in_both_modes_and_followed_only_in_audit_only(SanctumMode mode)
    {
        SanctumPipelineHarness harness = CreateHarness(mode, ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool("browse_web", clients), "browse_web",
            new Dictionary<string, object?> { ["url"] = InitialUrl }, TurnFor(harness, mode));

        Assert.False(processed.Denied);

        if (mode == SanctumMode.AuditOnly)
        {
            Assert.Contains("controlled second-hop page", processed.ResultText, StringComparison.Ordinal);

            Assert.Equal([InitialUrl, PublicTarget], http.Requests);
        }
        else
        {
            Assert.Contains("WebBrowsing.SsrfBlocked", processed.ResultText, StringComparison.Ordinal);

            Assert.DoesNotContain("controlled second-hop page", processed.ResultText, StringComparison.Ordinal);

            Assert.Equal([InitialUrl], http.Requests);
        }

        AssertOneTargetBreach(harness, PublicTarget, "browse_web");
    }

    public void Dispose() =>
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(ToolExecutionPipelineSanctumRedirectModeTests));

    private static AITool CreateTool(string toolName, IHttpClientFactory clients) => toolName == ArcanumReadUrlTool.ToolName
        ? new ArcanumReadUrlTool(new SingleProviderCatalog(CreateProvider(clients)), new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()))
        : new ArcanumBrowseWebTool(clients, new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { WebBrowsing = true } }),
            NullLogger.Instance, null, Dns());

    private static ToolExecutionPipeline.TurnContext TurnFor(SanctumPipelineHarness harness, SanctumMode mode) => new()
    {
        Campaign = harness.Campaign,
        CampaignId = harness.CampaignId,
        WorkspaceRoot = harness.WorkspaceRoot,
        SanctumEnabled = true,
        SanctumMode = mode,
    };

    private SanctumPipelineHarness CreateHarness(SanctumMode mode, string[] allowedDomains)
    {
        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(_root, allowedDomains);

        // The real guard reloads this in-memory campaign config; its mode agrees with the turn.
        CampaignRepository.SetSanctumConfig(harness.Campaign, new SanctumConfig
        {
            Enabled = true,
            Mode = mode,
            EnforcePathBoundary = true,
            NetworkPolicy = NetworkPolicy.AllowList,
            AllowedDomains = allowedDomains,
        });

        Assert.Equal(mode, CampaignRepository.GetSanctumConfig(harness.Campaign).Mode);

        return harness;
    }

    private static FakeDnsResolver Dns()
    {
        FakeDnsResolver dns = new();

        dns.Add("allowed.test", IPAddress.Parse("93.184.216.34"));

        dns.Add("off-list.test", IPAddress.Parse("93.184.216.35"));

        dns.Add("private.test", IPAddress.Parse("10.1.2.3"));

        return dns;
    }

    private static LocalHttpWebProvider CreateProvider(IHttpClientFactory clients) =>
        new(clients, new WebPageContentExtractor(), Dns());

    private static async Task<ToolExecutionPipeline.ProcessedToolCall> RunReadUrlAsync(
        SanctumPipelineHarness harness,
        LocalHttpWebProvider provider,
        SanctumMode mode)
    {
        // This real-guard precondition isolates the redirect defect: initial URL has no violation.
        SanctumResult initial = await harness.Guard.ValidateNetworkAsync(harness.CampaignId, InitialUrl, ArcanumReadUrlTool.ToolName);

        Assert.True(initial.Allowed);

        Assert.Empty(harness.Breaches.Records);

        ArcanumReadUrlTool tool = new(new SingleProviderCatalog(provider), new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

        ToolExecutionPipeline.TurnContext turn = new()
        {
            Campaign = harness.Campaign,
            CampaignId = harness.CampaignId,
            WorkspaceRoot = harness.WorkspaceRoot,
            SanctumEnabled = true,
            SanctumMode = mode,
        };

        Assert.Equal(CampaignRepository.GetSanctumConfig(harness.Campaign).Mode, turn.SanctumMode);

        return await harness.ProcessAsync(tool, ArcanumReadUrlTool.ToolName, new Dictionary<string, object?> { ["url"] = InitialUrl }, turn);
    }

    private static void AssertReadRefused(string resultText)
    {
        using JsonDocument result = JsonDocument.Parse(resultText);

        Assert.Equal("error", result.RootElement.GetProperty("status").GetString());

        Assert.Equal("WebResearch.SsrfBlocked", result.RootElement.GetProperty("code").GetString());

        Assert.DoesNotContain("controlled second-hop page", resultText, StringComparison.Ordinal);
    }

    private static void AssertOneTargetBreach(SanctumPipelineHarness harness, string target, string toolName = ArcanumReadUrlTool.ToolName)
    {
        SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

        Assert.Equal("NetworkEgress", breach.BreachType);

        Assert.Equal(toolName, breach.ToolName);

        Assert.Equal(harness.CampaignId, breach.CampaignId);

        Assert.NotNull(breach.Details);

        Assert.Equal(target, breach.Details!.RequestedUrl);
    }

    private sealed class SingleProviderCatalog(LocalHttpWebProvider provider) : IWebResearchProviderCatalog
    {
        public bool TryGetProvider(string providerName, [NotNullWhen(true)] out IWebResearchProvider? found)
        {
            found = string.Equals(providerName, provider.ProviderName, StringComparison.OrdinalIgnoreCase) ? provider : null;

            return found is not null;
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(handler, disposeHandler: false);

        public HttpClient CreateClient(string name) => _client;

        public void Dispose() => _client.Dispose();
    }

    // Supplies only deterministic HTTP I/O. The real LocalHttpWebProvider decides whether to
    // request the redirect target after invoking the ambient ward and baseline address guard.
    private sealed class RedirectScriptHandler(string target) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string url = request.RequestUri!.AbsoluteUri;

            Requests.Add(url);

            if (url == InitialUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri(target) },
                });
            }

            if (url == target)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("controlled second-hop page", Encoding.UTF8, "text/plain"),
                });
            }

            throw new InvalidOperationException("The native reader requested an unexpected test URL.");
        }
    }
}
