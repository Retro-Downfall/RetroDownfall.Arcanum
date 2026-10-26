using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
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

/// <summary>
/// What an audit-only Sanctum records for one tool call. The breach row is the only control an audit-only Sanctum has, so a call that
/// breaks several rules at once (a disabled tool aimed at an off-list host, a script run from several roots outside the boundary) leaves one
/// row for each, not one row for the first and silence about the rest. A strict Sanctum stops at the first refusal, and either mode stops
/// the moment the guard cannot evaluate the policy or cannot keep the row.
/// </summary>
public sealed class ToolExecutionPipelineSanctumAuditOnlyTests : IDisposable
{
    private const string OffListTarget = "https://off-list.test/page";

    private const string AllowedTarget = "https://allowed.test/page";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-sanctum-audit-only-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("read_url")]
    [InlineData("browse_web")]
    public async Task AuditOnly_records_the_disabled_tool_and_the_off_list_target_of_one_call_and_lets_it_run(string toolName)
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, disabledTools: [toolName]);

        using ScriptedPage http = new();

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool(toolName, clients), toolName,
            new Dictionary<string, object?> { ["url"] = OffListTarget }, TurnFor(harness, SanctumMode.AuditOnly));

        Assert.False(processed.Denied);

        Assert.Contains("controlled page", processed.ResultText, StringComparison.Ordinal);

        Assert.Equal([OffListTarget], http.Requests);

        Assert.Equal(
            ["DisabledTool", "NetworkEgress"],
            harness.Breaches.Records.Select(static record => record.BreachType).Order(StringComparer.Ordinal));

        Assert.All(harness.Breaches.Records, record => Assert.Equal(toolName, record.ToolName));
    }

    [Theory]
    [InlineData("read_url")]
    [InlineData("browse_web")]
    public async Task AuditOnly_records_only_the_disabled_tool_when_the_target_is_allowed(string toolName)
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, disabledTools: [toolName]);

        using ScriptedPage http = new();

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool(toolName, clients), toolName,
            new Dictionary<string, object?> { ["url"] = AllowedTarget }, TurnFor(harness, SanctumMode.AuditOnly));

        Assert.False(processed.Denied);

        Assert.Equal([AllowedTarget], http.Requests);

        Assert.Equal("DisabledTool", Assert.Single(harness.Breaches.Records).BreachType);
    }

    [Theory]
    [InlineData("read_url")]
    [InlineData("browse_web")]
    public async Task Strict_stops_at_the_first_refusal_of_a_call_that_is_both_disabled_and_off_list(string toolName)
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.Strict, disabledTools: [toolName]);

        using ScriptedPage http = new();

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool(toolName, clients), toolName,
            new Dictionary<string, object?> { ["url"] = OffListTarget }, TurnFor(harness, SanctumMode.Strict));

        Assert.True(processed.Denied);

        Assert.Empty(http.Requests);

        Assert.Equal("DisabledTool", Assert.Single(harness.Breaches.Records).BreachType);
    }

    [Fact]
    public async Task AuditOnly_stops_at_a_breach_it_cannot_record_and_refuses_the_call()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly, disabledTools: ["read_url"]);

        harness.Breaches.RecordFailure = new IOException("simulated breach store failure");

        using ScriptedPage http = new();

        using SingleClientFactory clients = new(http);

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.ProcessAsync(CreateTool("read_url", clients), "read_url",
            new Dictionary<string, object?> { ["url"] = OffListTarget }, TurnFor(harness, SanctumMode.AuditOnly));

        Assert.True(processed.Denied);

        Assert.Contains("could not be recorded", processed.ResultText, StringComparison.Ordinal);

        Assert.Empty(http.Requests);

        Assert.Empty(harness.Breaches.Records);
    }

    // run_spell_script resolves its script under every candidate root, and every one of them is a place the script may run from. Each root
    // outside the boundary is its own breach, and the audit row of the first must not stand for all of them.
    [Fact]
    public async Task AuditOnly_records_every_script_root_outside_the_boundary_and_lets_the_call_run()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.AuditOnly);

        string outsideOne = Path.Combine(Path.GetTempPath(), "arcanum-audit-outside-one-" + Guid.NewGuid().ToString("N"));

        string outsideTwo = Path.Combine(Path.GetTempPath(), "arcanum-audit-outside-two-" + Guid.NewGuid().ToString("N"));

        ToolExecutionPipeline.TurnContext turn = TurnFor(harness, SanctumMode.AuditOnly, [outsideOne, outsideTwo]);

        (ToolExecutionPipeline.ProcessedToolCall processed, bool invoked) = await harness.ProcessStandInAsync(
            "run_spell_script",
            new Dictionary<string, object?> { ["script_name"] = "build.sh" },
            turn);

        Assert.False(processed.Denied);

        Assert.True(invoked);

        Assert.All(harness.Breaches.Records, static record => Assert.Equal("PathEscape", record.BreachType));

        // Per root: the script path and the working directory it would run from.
        string[] paths = [.. harness.Breaches.Records.Select(static record => record.Details?.RequestedPath ?? string.Empty)];

        Assert.Equal(4, paths.Length);

        Assert.Equal(2, paths.Count(static path => path.Contains("outside-one", StringComparison.Ordinal)));

        Assert.Equal(2, paths.Count(static path => path.Contains("outside-two", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Strict_stops_at_the_first_script_root_outside_the_boundary()
    {
        SanctumPipelineHarness harness = CreateHarness(SanctumMode.Strict);

        string outsideOne = Path.Combine(Path.GetTempPath(), "arcanum-audit-outside-one-" + Guid.NewGuid().ToString("N"));

        string outsideTwo = Path.Combine(Path.GetTempPath(), "arcanum-audit-outside-two-" + Guid.NewGuid().ToString("N"));

        ToolExecutionPipeline.TurnContext turn = TurnFor(harness, SanctumMode.Strict, [outsideOne, outsideTwo]);

        (ToolExecutionPipeline.ProcessedToolCall processed, bool invoked) = await harness.ProcessStandInAsync(
            "run_spell_script",
            new Dictionary<string, object?> { ["script_name"] = "build.sh" },
            turn);

        Assert.True(processed.Denied);

        Assert.False(invoked);

        Assert.Equal("PathEscape", Assert.Single(harness.Breaches.Records).BreachType);
    }

    // A patch whose touched paths cannot be listed must be repaired before the guard can evaluate it.
    // Both modes refuse the malformed arguments without inventing a policy breach.
    [Theory]
    [InlineData(SanctumMode.Strict)]
    [InlineData(SanctumMode.AuditOnly)]
    public void A_patch_whose_paths_cannot_be_listed_is_refused_in_both_modes(SanctumMode mode)
    {
        SanctumResult denial = ToolExecutionPipeline.UnparsablePatchDenial(new(false, null, "invalid_patch", "The unified diff is empty."));

        Assert.False(denial.Allowed);

        Assert.False(SanctumEnforcement.AllowsContinuation(denial, mode));

        Assert.Null(denial.Breach);

        Assert.Equal(SanctumDenialKind.InvalidArguments, denial.DenialKind);
    }

    public void Dispose() =>
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(ToolExecutionPipelineSanctumAuditOnlyTests));

    private SanctumPipelineHarness CreateHarness(SanctumMode mode, string[]? disabledTools = null)
    {
        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(_root, ["allowed.test"]);

        CampaignRepository.SetSanctumConfig(harness.Campaign, new SanctumConfig
        {
            Enabled = true,
            Mode = mode,
            EnforcePathBoundary = true,
            NetworkPolicy = NetworkPolicy.AllowList,
            AllowedDomains = ["allowed.test"],
            DisabledTools = disabledTools ?? [],
        });

        return harness;
    }

    private static ToolExecutionPipeline.TurnContext TurnFor(
        SanctumPipelineHarness harness,
        SanctumMode mode,
        IReadOnlyList<string>? spellScriptRoots = null) => new()
        {
            Campaign = harness.Campaign,
            CampaignId = harness.CampaignId,
            WorkspaceRoot = harness.WorkspaceRoot,
            SanctumEnabled = true,
            SanctumMode = mode,
            SpellScriptRoots = spellScriptRoots ?? [],
        };

    private static FakeDnsResolver Dns()
    {
        FakeDnsResolver dns = new();

        dns.Add("allowed.test", IPAddress.Parse("93.184.216.34"));

        dns.Add("off-list.test", IPAddress.Parse("93.184.216.35"));

        return dns;
    }

    private static AITool CreateTool(string toolName, IHttpClientFactory clients) => toolName == ArcanumReadUrlTool.ToolName
        ? new ArcanumReadUrlTool(
            new SingleProviderCatalog(new LocalHttpWebProvider(clients, new WebPageContentExtractor(), Dns())),
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()))
        : new ArcanumBrowseWebTool(
            clients,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { WebBrowsing = true } }),
            NullLogger.Instance,
            null,
            Dns());

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

    /// <summary>The origin: records every URL requested and answers a small text page.</summary>
    private sealed class ScriptedPage : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("controlled page", Encoding.UTF8, "text/plain"),
                });
        }
    }
}
