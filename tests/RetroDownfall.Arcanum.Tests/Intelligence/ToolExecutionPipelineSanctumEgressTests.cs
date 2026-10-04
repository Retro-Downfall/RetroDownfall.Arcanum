using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// R-055: pins the only link between the pipeline's Sanctum preflight and the native web and script
/// tools. The ward travels through an AsyncLocal, so deleting any one of the publisher, the options
/// assignment or the per-tool preflight cases used to turn no test red. These tests run the real
/// <see cref="SanctumGuard"/> (with in-memory repositories) so a denial is observed as the recorded
/// breach a campaign operator would see.
/// </summary>
public sealed class ToolExecutionPipelineSanctumEgressTests : IDisposable
{
    private const string AllowedHost = "allowed.test";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-sanctum-egress-" + Guid.NewGuid().ToString("N"));

    public ToolExecutionPipelineSanctumEgressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort fixture cleanup.
        }
    }

    [Fact]
    public async Task ReadUrl_InStrictSanctum_PublishesWardAndOptionsCarryIt()
    {
        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(_root, [AllowedHost]);
        RecordingReadProvider provider = new();

        ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(
            harness,
            provider,
            "https://allowed.test/page",
            harness.StrictTurnContext());

        Assert.False(processed.Denied);

        Assert.Equal(1, provider.ReadCount);

        Func<Uri, CancellationToken, ValueTask<bool>>? ward = provider.LastOptions?.RedirectEgressWard;

        Assert.NotNull(ward);

        Assert.Empty(harness.Breaches.Records);

        Assert.True(
            await ward(new Uri("https://allowed.test/hop"), CancellationToken.None));

        Assert.Empty(harness.Breaches.Records);

        Assert.False(
            await ward(new Uri("https://off-list.test/hop"), CancellationToken.None));

        SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

        Assert.Equal("NetworkEgress", breach.BreachType);

        Assert.Equal(ArcanumReadUrlTool.ToolName, breach.ToolName);

        Assert.Equal(harness.CampaignId, breach.CampaignId);
    }

    [Fact]
    public async Task ReadUrl_OffAllowlistFirstHop_IsDeniedBeforeToolRuns()
    {
        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(_root, [AllowedHost]);
        RecordingReadProvider provider = new();

        ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(
            harness,
            provider,
            "https://off-list.test/page",
            harness.StrictTurnContext());

        Assert.True(processed.Denied);

        Assert.Equal(0, provider.ReadCount);

        Assert.Null(provider.LastOptions);

        SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

        Assert.Equal("NetworkEgress", breach.BreachType);

        Assert.Equal(ArcanumReadUrlTool.ToolName, breach.ToolName);
    }

    [Fact]
    public async Task ReadUrl_NoSanctum_PublishesNoWard()
    {
        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(_root, [AllowedHost]);

        ToolExecutionPipeline.TurnContext[] unrestricted =
        [
            new(),
            new()
            {
                Campaign = harness.Campaign,
                CampaignId = harness.CampaignId,
                WorkspaceRoot = _root,
                SanctumEnabled = false,
                SanctumMode = SanctumMode.Strict,
            },
        ];

        foreach (ToolExecutionPipeline.TurnContext turnContext in unrestricted)
        {
            RecordingReadProvider provider = new();

            ToolExecutionPipeline.ProcessedToolCall processed = await RunReadUrlAsync(
                harness,
                provider,
                "https://off-list.test/page",
                turnContext);

            Assert.False(processed.Denied);

            Assert.Equal(1, provider.ReadCount);

            Assert.NotNull(provider.LastOptions);

            Assert.Null(provider.LastOptions.RedirectEgressWard);
        }

        Assert.Empty(harness.Breaches.Records);
    }

    [Fact]
    public async Task RunSpellScript_ScriptUnderEveryResonantRoot_IsSanctumValidated()
    {
        string workspace = Path.Combine(_root, "workspace");

        string activeRoot = Path.Combine(workspace, "spells", "active", "scripts");

        string resonantRoot = Path.Combine(workspace, "spells", "resonant", "scripts");

        Directory.CreateDirectory(activeRoot);

        Directory.CreateDirectory(resonantRoot);

        SanctumPipelineHarness harness = SanctumPipelineHarness.Create(workspace, [AllowedHost]);

        IDictionary<string, object?> arguments = new Dictionary<string, object?>
        {
            ["script_name"] = "build.sh",
        };

        (ToolExecutionPipeline.ProcessedToolCall allowed, bool allowedInvoked) =
            await harness.ProcessStandInAsync(
                "run_spell_script",
                arguments,
                harness.StrictTurnContext(spellScriptRoots: [activeRoot, resonantRoot]));

        Assert.False(allowed.Denied);

        Assert.True(allowedInvoked);

        string[] validatedScripts = harness.Guard.PathChecks
            .Where(static check => check.OperationType == "script path")
            .Select(static check => check.RequestedPath)
            .ToArray();

        Assert.Equal(
            [
                Path.Combine(activeRoot, "build.sh"),
                Path.Combine(resonantRoot, "build.sh"),
            ],
            validatedScripts);

        string[] validatedRoots = harness.Guard.PathChecks
            .Where(static check => check.OperationType == "working directory")
            .Select(static check => check.RequestedPath)
            .ToArray();

        Assert.Equal([activeRoot, resonantRoot], validatedRoots);

        Assert.Empty(harness.Breaches.Records);

        string outsideRoot = Path.Combine(_root, "elsewhere", "scripts");

        Directory.CreateDirectory(outsideRoot);

        (ToolExecutionPipeline.ProcessedToolCall denied, bool deniedInvoked) =
            await harness.ProcessStandInAsync(
                "run_spell_script",
                arguments,
                harness.StrictTurnContext(spellScriptRoots: [activeRoot, outsideRoot]));

        Assert.True(denied.Denied);

        Assert.False(deniedInvoked);

        SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal("run_spell_script", breach.ToolName);
    }

    private static Task<ToolExecutionPipeline.ProcessedToolCall> RunReadUrlAsync(
        SanctumPipelineHarness harness,
        RecordingReadProvider provider,
        string url,
        ToolExecutionPipeline.TurnContext turnContext)
    {
        ArcanumReadUrlTool tool = new(
            new SingleProviderCatalog(provider),
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

        return harness.ProcessAsync(
            tool,
            ArcanumReadUrlTool.ToolName,
            new Dictionary<string, object?> { ["url"] = url },
            turnContext);
    }

    private sealed class RecordingReadProvider : IWebResearchProvider
    {
        public string ProviderName => WebResearchProviderNames.LocalHttp;

        public WebResearchCapabilities Capabilities => WebResearchCapabilities.ReadUrl;

        public int ReadCount { get; private set; }

        public WebReadOptions? LastOptions { get; private set; }

        public Task<Result<WebSearchResult>> SearchAsync(
            string query,
            WebSearchOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<WebReadResult>> ReadUrlAsync(
            string url,
            WebReadOptions options,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;

            LastOptions = options;

            return Task.FromResult(
                Result<WebReadResult>.Success(
                    new WebReadResult("Page", "Body text.", url, [])));
        }
    }

    private sealed class SingleProviderCatalog(IWebResearchProvider provider) : IWebResearchProviderCatalog
    {
        public bool TryGetProvider(
            string providerName,
            [NotNullWhen(true)] out IWebResearchProvider? found)
        {
            found = string.Equals(providerName, provider.ProviderName, StringComparison.OrdinalIgnoreCase)
                ? provider
                : null;

            return found is not null;
        }
    }
}
