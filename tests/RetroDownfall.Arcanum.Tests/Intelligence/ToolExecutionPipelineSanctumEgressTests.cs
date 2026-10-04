using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
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
        SanctumHarness harness = SanctumHarness.Create(_root, allowedDomains: [AllowedHost]);
        RecordingReadProvider provider = new();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RunReadUrlAsync(
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
        SanctumHarness harness = SanctumHarness.Create(_root, allowedDomains: [AllowedHost]);
        RecordingReadProvider provider = new();

        ToolExecutionPipeline.ProcessedToolCall processed = await harness.RunReadUrlAsync(
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
        SanctumHarness harness = SanctumHarness.Create(_root, allowedDomains: [AllowedHost]);

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

            ToolExecutionPipeline.ProcessedToolCall processed = await harness.RunReadUrlAsync(
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

        SanctumHarness harness = SanctumHarness.Create(workspace, allowedDomains: [AllowedHost]);

        ToolExecutionPipeline.TurnContext allRootsInside = harness.StrictTurnContext(
            spellScriptRoots: [activeRoot, resonantRoot]);

        (ToolExecutionPipeline.ProcessedToolCall allowed, bool allowedInvoked) =
            await harness.RunSpellScriptAsync("build.sh", allRootsInside);

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

        ToolExecutionPipeline.TurnContext secondRootOutside = harness.StrictTurnContext(
            spellScriptRoots: [activeRoot, outsideRoot]);

        (ToolExecutionPipeline.ProcessedToolCall denied, bool deniedInvoked) =
            await harness.RunSpellScriptAsync("build.sh", secondRootOutside);

        Assert.True(denied.Denied);

        Assert.False(deniedInvoked);

        SanctumBreachRecord breach = Assert.Single(harness.Breaches.Records);

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal("run_spell_script", breach.ToolName);
    }

    private sealed class SanctumHarness
    {
        private readonly string _workspaceRoot;

        private SanctumHarness(
            string workspaceRoot,
            Campaign campaign,
            RecordingSanctumGuard guard,
            InMemoryBreachRepository breaches)
        {
            _workspaceRoot = workspaceRoot;

            Campaign = campaign;

            Guard = guard;

            Breaches = breaches;
        }

        public Campaign Campaign { get; }

        public string CampaignId => Campaign.Id.ToString("D");

        public RecordingSanctumGuard Guard { get; }

        public InMemoryBreachRepository Breaches { get; }

        public static SanctumHarness Create(string workspaceRoot, string[] allowedDomains)
        {
            _ = Directory.CreateDirectory(workspaceRoot);

            SanctumConfig config = new()
            {
                Enabled = true,
                Mode = SanctumMode.Strict,
                EnforcePathBoundary = true,
                NetworkPolicy = NetworkPolicy.AllowList,
                AllowedDomains = allowedDomains,
            };

            Campaign campaign = new()
            {
                Id = Guid.NewGuid(),
                Name = "egress-campaign",
                NameLower = "egress-campaign",
                Path = workspaceRoot,
                Type = WorkspaceType.Campaign,
                SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(config),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            InMemoryBreachRepository breaches = new();

            RecordingSanctumGuard guard = new(
                new SanctumGuard(
                    new SingleCampaignRepository(campaign),
                    breaches,
                    NullLogger<SanctumGuard>.Instance,
                    new FakeDnsResolver()));

            return new SanctumHarness(workspaceRoot, campaign, guard, breaches);
        }

        public ToolExecutionPipeline.TurnContext StrictTurnContext(
            IReadOnlyList<string>? spellScriptRoots = null) =>
            new()
            {
                Campaign = Campaign,
                CampaignId = CampaignId,
                WorkspaceRoot = _workspaceRoot,
                SanctumEnabled = true,
                SanctumMode = SanctumMode.Strict,
                SpellScriptRoots = spellScriptRoots ?? [],
            };

        public async Task<ToolExecutionPipeline.ProcessedToolCall> RunReadUrlAsync(
            RecordingReadProvider provider,
            string url,
            ToolExecutionPipeline.TurnContext turnContext)
        {
            ArcanumReadUrlTool tool = new(
                new SingleProviderCatalog(provider),
                new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));

            return await CreatePipeline().ProcessSingleToolCallAsync(
                new FunctionCallContent(
                    "call-read-url",
                    ArcanumReadUrlTool.ToolName,
                    new Dictionary<string, object?> { ["url"] = url }),
                new PingRequest("read", WorkingDirectory: _workspaceRoot),
                new ChatOptions { Tools = [tool] },
                activeSpell: null,
                sessionId: "session-egress",
                turnContext,
                suppressInvocationFailures: false,
                CancellationToken.None);
        }

        public async Task<(ToolExecutionPipeline.ProcessedToolCall Processed, bool Invoked)> RunSpellScriptAsync(
            string scriptName,
            ToolExecutionPipeline.TurnContext turnContext)
        {
            bool invoked = false;

            AIFunction tool = AIFunctionFactory.Create(
                (string script_name) =>
                {
                    invoked = true;

                    return "ran " + script_name;
                },
                "run_spell_script");

            ToolExecutionPipeline.ProcessedToolCall processed =
                await CreatePipeline().ProcessSingleToolCallAsync(
                    new FunctionCallContent(
                        "call-run-spell-script",
                        "run_spell_script",
                        new Dictionary<string, object?> { ["script_name"] = scriptName }),
                    new PingRequest("run", WorkingDirectory: _workspaceRoot),
                    new ChatOptions { Tools = [tool] },
                    activeSpell: null,
                    sessionId: "session-egress",
                    turnContext,
                    suppressInvocationFailures: false,
                    CancellationToken.None);

            return (processed, invoked);
        }

        private ToolExecutionPipeline CreatePipeline() =>
            new(
                new TestOptionsSnapshot<ArcanumSettings>(
                    new ArcanumSettings
                    {
                        Security = new SecuritySettings
                        {
                            Ward = new WardPolicySettings { ForbiddenArts = [] },
                        },
                    }),
                new RecordOnlyWard(),
                Guard,
                new NoOpSessionAttachmentStore(),
                NullLogger<ToolExecutionPipeline>.Instance);
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

    /// <summary>Delegates to the real guard while recording every path check the pipeline makes.</summary>
    private sealed class RecordingSanctumGuard(ISanctumGuard inner) : ISanctumGuard
    {
        public List<(string RequestedPath, string OperationType, string ToolName)> PathChecks { get; } = [];

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default)
        {
            PathChecks.Add((requestedPath, operationType, toolName));

            return inner.ValidatePathAsync(campaignId, requestedPath, operationType, toolName, ct);
        }

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            inner.ValidateNetworkAsync(campaignId, url, toolName, ct);

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            inner.ValidateToolAsync(campaignId, toolName, ct);

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            inner.GetEffectiveResourceLimitsForWorkspaceAsync(workspaceRoot, ct);

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            inner.GetChildProcessBoundaryForWorkspaceAsync(workspaceRoot, ct);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            inner.RecordResourceLimitBreachAsync(
                workspaceRoot,
                toolName,
                resource,
                limitValue,
                actualValue,
                ct);
    }

    private sealed class InMemoryBreachRepository : ISanctumBreachRepository
    {
        public List<SanctumBreachRecord> Records { get; } = [];

        public Task RecordAsync(
            SanctumBreachRecord breach,
            int maxBreachCount,
            CancellationToken ct = default)
        {
            Records.Add(breach);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SanctumBreachRecord>> QueryAsync(
            string campaignId,
            int limit,
            DateTimeOffset? before = null,
            string? toolName = null,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SanctumBreachRecord>>(
                Records.Where(record => record.CampaignId == campaignId).Take(limit).ToArray());

        public Task<int> GetCountAsync(string campaignId, CancellationToken ct = default) =>
            Task.FromResult(Records.Count(record => record.CampaignId == campaignId));

        public Task<int> DeleteOldestAsync(string campaignId, int count, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class SingleCampaignRepository(Campaign campaign) : ICampaignRepository
    {
        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(id == campaign.Id ? campaign : null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign added, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Campaign> UpdateAsync(Campaign updated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class RecordOnlyWard : IWard
    {
        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The ward is record-only; nothing may wait on it.");

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) => ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin) =>
            new(allowed, reason, DateTimeOffset.UtcNow, origin);

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];
    }
}
