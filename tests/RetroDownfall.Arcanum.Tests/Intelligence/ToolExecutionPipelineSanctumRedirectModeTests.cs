using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
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
using RetroDownfall.Arcanum.Infrastructure.Security;
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

    // An audit-only Sanctum records the breach of a redirect hop that leaves the allowed domains and refuses it all the same, exactly as a
    // Strict one does: a hop's destination is chosen by the remote server, not named by the model, so recording it and carrying on is not
    // a trade the operator asked for. The first target is the other case, and it is recorded and allowed in AuditOnly (see below).
    [Fact]
    public async Task AuditOnly_public_off_list_redirect_is_refused_before_second_request_and_records_one_breach()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        LocalHttpWebProvider provider = CreateProvider(clients);

        ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(harness, provider, SanctumMode.AuditOnly);

        Assert.False(processed.Denied);

        AssertReadRefused(processed.ResultText);

        Assert.Equal([InitialUrl], http.Requests);

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

    // The initial target of read_url and of the legacy alias is the one target an audit-only Sanctum lets through: a target outside the
    // allowed domains is recorded as a breach in both modes, refused before any request in Strict, and fetched in AuditOnly. A redirect
    // hop and a research citation are not asked this question; they are refused in both modes.
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

    // The legacy alias publishes the same ward as read_url, so its redirect hop is recorded as a breach and refused in both modes.
    [Theory]
    [InlineData(SanctumMode.AuditOnly)]
    [InlineData(SanctumMode.Strict)]
    public async Task The_legacy_alias_redirect_outside_the_allowed_domains_is_recorded_and_refused_in_both_modes(SanctumMode mode)
    {
        SanctumPipelineHarness harness = CreateHarness(mode, ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool("browse_web", clients), "browse_web",
            new Dictionary<string, object?> { ["url"] = InitialUrl }, TurnFor(harness, mode));

        Assert.False(processed.Denied);

        Assert.Contains("WebBrowsing.SsrfBlocked", processed.ResultText, StringComparison.Ordinal);

        Assert.DoesNotContain("controlled second-hop page", processed.ResultText, StringComparison.Ordinal);

        Assert.Equal([InitialUrl], http.Requests);

        AssertOneTargetBreach(harness, PublicTarget, "browse_web");
    }

    // The breach row is the only control an audit-only Sanctum has. A breach the store cannot keep is refused rather than waved through on a
    // log line, at the initial target and at every redirect hop.
    [Theory]
    [InlineData("read_url")]
    [InlineData("browse_web")]
    public async Task AuditOnly_refuses_the_initial_target_when_the_breach_cannot_be_recorded_and_sends_nothing(string toolName)
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, ["allowed.test"]);

        harness.Breaches.RecordFailure = new IOException("simulated breach store failure");

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool(toolName, clients), toolName,
            new Dictionary<string, object?> { ["url"] = PublicTarget }, TurnFor(harness, SanctumMode.AuditOnly));

        Assert.True(processed.Denied);

        Assert.Contains("The Sanctum Guard has blocked this action: NetworkEgress", processed.ResultText, StringComparison.Ordinal);

        Assert.Contains("could not be recorded", processed.ResultText, StringComparison.Ordinal);

        Assert.Empty(http.Requests);

        Assert.Empty(harness.Breaches.Records);
    }

    [Fact]
    public async Task AuditOnly_does_not_follow_an_off_list_redirect_when_the_breach_cannot_be_recorded()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, ["allowed.test"]);

        harness.Breaches.RecordFailure = new IOException("simulated breach store failure");

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(harness, CreateProvider(clients), SanctumMode.AuditOnly);

        Assert.False(processed.Denied);

        AssertReadRefused(processed.ResultText);

        Assert.Equal([InitialUrl], http.Requests);

        Assert.Empty(harness.Breaches.Records);
    }

    // A campaign the guard cannot read has no policy to audit. AuditOnly cannot be honoured for it, so the target is refused in both modes.
    [Theory]
    [InlineData("read_url", SanctumMode.Strict)]
    [InlineData("read_url", SanctumMode.AuditOnly)]
    [InlineData("browse_web", SanctumMode.Strict)]
    [InlineData("browse_web", SanctumMode.AuditOnly)]
    public async Task A_campaign_the_guard_cannot_resolve_refuses_the_target_in_both_modes_and_sends_nothing(string toolName, SanctumMode mode)
    {
        SanctumPipelineHarness harness = CreateHarness(mode, ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.TurnContext turn = new()
        {
            Campaign = harness.Campaign,
            CampaignId = Guid.NewGuid().ToString("D"),
            WorkspaceRoot = harness.WorkspaceRoot,
            SanctumEnabled = true,
            SanctumMode = mode,
        };

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool(toolName, clients), toolName,
            new Dictionary<string, object?> { ["url"] = PublicTarget }, turn);

        Assert.True(processed.Denied);

        Assert.Contains("The Sanctum Guard has blocked this action", processed.ResultText, StringComparison.Ordinal);

        Assert.Empty(http.Requests);

        Assert.Empty(harness.Breaches.Records);
    }

    // The baseline address guard is independent of the Sanctum mode. An AuditOnly Sanctum records an off-list initial target and lets the
    // tool go on, and the tool still refuses a target that resolves to a private address before any request is made.
    [Theory]
    [InlineData("read_url", false)]
    [InlineData("read_url", true)]
    [InlineData("browse_web", false)]
    [InlineData("browse_web", true)]
    public async Task AuditOnly_initial_target_that_resolves_privately_is_refused_with_no_request(string toolName, bool campaignAllowsTheHost)
    {
        SanctumPipelineHarness harness = CreateHarness(
            SanctumMode.AuditOnly,
            campaignAllowsTheHost ? ["allowed.test", "private.test"] : ["allowed.test"]);

        using RedirectScriptHandler http = new(PublicTarget);

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool(toolName, clients), toolName,
            new Dictionary<string, object?> { ["url"] = PrivateTarget }, TurnFor(harness, SanctumMode.AuditOnly));

        Assert.False(processed.Denied);

        string ssrfCode = toolName == ArcanumReadUrlTool.ToolName ? "WebResearch.SsrfBlocked" : "WebBrowsing.SsrfBlocked";

        Assert.Contains(ssrfCode, processed.ResultText, StringComparison.Ordinal);

        Assert.DoesNotContain("controlled second-hop page", processed.ResultText, StringComparison.Ordinal);

        Assert.Empty(http.Requests);

        if (campaignAllowsTheHost)
        {
            Assert.Empty(harness.Breaches.Records);
        }
        else
        {
            AssertOneTargetBreach(harness, PrivateTarget, toolName);
        }
    }

    // DNS rebinding: the name resolves to a public address when the tool validates it and to loopback when the connection is made. The real
    // untrusted-egress handler re-validates the address it is about to dial, so nothing reaches the loopback listener, in either mode and
    // whether or not the campaign allows the host.
    [Theory]
    [InlineData("read_url", SanctumMode.AuditOnly, false)]
    [InlineData("read_url", SanctumMode.AuditOnly, true)]
    [InlineData("read_url", SanctumMode.Strict, true)]
    [InlineData("browse_web", SanctumMode.AuditOnly, false)]
    [InlineData("browse_web", SanctumMode.AuditOnly, true)]
    [InlineData("browse_web", SanctumMode.Strict, true)]
    public async Task A_target_that_rebinds_to_loopback_at_connect_time_is_refused_and_nothing_is_dialed(string toolName, SanctumMode mode, bool campaignAllowsTheHost)
    {
        using TcpListener loopback = new(IPAddress.Loopback, 0);

        loopback.Start();

        string target = $"http://rebind.test:{((IPEndPoint)loopback.LocalEndpoint).Port}/page";

        SanctumPipelineHarness harness = CreateHarness(mode, campaignAllowsTheHost ? ["allowed.test", "rebind.test"] : ["allowed.test"]);

        FakeDnsResolver validation = new();

        validation.Add("rebind.test", IPAddress.Parse("93.184.216.36"));

        FakeDnsResolver connect = new();

        connect.Add("rebind.test", IPAddress.Loopback);

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(connect);

        using SingleClientFactory clients = new(handler);

        AITool tool = toolName == ArcanumReadUrlTool.ToolName
            ? new ArcanumReadUrlTool(
                new SingleProviderCatalog(new LocalHttpWebProvider(clients, new WebPageContentExtractor(), validation)),
                new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()))
            : new ArcanumBrowseWebTool(
                clients,
                new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { WebBrowsing = true } }),
                NullLogger.Instance,
                null,
                validation);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(tool, toolName,
            new Dictionary<string, object?> { ["url"] = target }, TurnFor(harness, mode));

        string ssrfCode = toolName == ArcanumReadUrlTool.ToolName ? "WebResearch.SsrfBlocked" : "WebBrowsing.SsrfBlocked";

        Assert.Contains(ssrfCode, processed.ResultText, StringComparison.Ordinal);

        Assert.False(loopback.Pending());

        if (campaignAllowsTheHost)
        {
            Assert.Empty(harness.Breaches.Records);
        }
        else
        {
            AssertOneTargetBreach(harness, target, toolName);
        }
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
