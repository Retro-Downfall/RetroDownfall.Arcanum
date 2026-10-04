using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
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

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Runs <see cref="ToolExecutionPipeline"/> against the real <see cref="SanctumGuard"/> with in-memory
/// repositories, so a Sanctum denial is observed as the breach record an operator would see rather
/// than as a stub's canned answer.
/// </summary>
internal sealed class SanctumPipelineHarness
{
    private SanctumPipelineHarness(
        string workspaceRoot,
        Campaign campaign,
        RecordingSanctumGuard guard,
        InMemorySanctumBreachRepository breaches)
    {
        WorkspaceRoot = workspaceRoot;

        Campaign = campaign;

        Guard = guard;

        Breaches = breaches;
    }

    public string WorkspaceRoot { get; }

    public Campaign Campaign { get; }

    public string CampaignId => Campaign.Id.ToString("D");

    public RecordingSanctumGuard Guard { get; }

    public InMemorySanctumBreachRepository Breaches { get; }

    public static SanctumPipelineHarness Create(string workspaceRoot, string[]? allowedDomains = null)
    {
        _ = Directory.CreateDirectory(workspaceRoot);

        SanctumConfig config = new()
        {
            Enabled = true,
            Mode = SanctumMode.Strict,
            EnforcePathBoundary = true,
            NetworkPolicy = NetworkPolicy.AllowList,
            AllowedDomains = allowedDomains ?? [],
        };

        Campaign campaign = new()
        {
            Id = Guid.NewGuid(),
            Name = "pipeline-campaign",
            NameLower = "pipeline-campaign",
            Path = workspaceRoot,
            Type = WorkspaceType.Campaign,
            SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(config),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        InMemorySanctumBreachRepository breaches = new();

        RecordingSanctumGuard guard = new(
            new SanctumGuard(
                new SingleCampaignRepository(campaign),
                breaches,
                NullLogger<SanctumGuard>.Instance,
                new FakeDnsResolver()));

        return new SanctumPipelineHarness(workspaceRoot, campaign, guard, breaches);
    }

    public ToolExecutionPipeline.TurnContext StrictTurnContext(
        IReadOnlyList<string>? spellScriptRoots = null) =>
        new()
        {
            Campaign = Campaign,
            CampaignId = CampaignId,
            WorkspaceRoot = WorkspaceRoot,
            SanctumEnabled = true,
            SanctumMode = SanctumMode.Strict,
            SpellScriptRoots = spellScriptRoots ?? [],
        };

    public ToolExecutionPipeline CreatePipeline() =>
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

    public Task<ToolExecutionPipeline.ProcessedToolCall> ProcessAsync(
        AITool tool,
        string toolName,
        IDictionary<string, object?> arguments,
        ToolExecutionPipeline.TurnContext turnContext) =>
        CreatePipeline().ProcessSingleToolCallAsync(
            new FunctionCallContent($"call-{toolName}", toolName, arguments),
            new PingRequest("run", WorkingDirectory: WorkspaceRoot),
            new ChatOptions { Tools = [tool] },
            activeSpell: null,
            sessionId: "session-sanctum-harness",
            turnContext,
            suppressInvocationFailures: false,
            CancellationToken.None);

    /// <summary>Registers a stand-in for <paramref name="toolName"/> that records whether it ran.</summary>
    public async Task<(ToolExecutionPipeline.ProcessedToolCall Processed, bool Invoked)> ProcessStandInAsync(
        string toolName,
        IDictionary<string, object?> arguments,
        ToolExecutionPipeline.TurnContext turnContext)
    {
        bool invoked = false;

        AIFunction tool = AIFunctionFactory.Create(
            () =>
            {
                invoked = true;

                return "ran";
            },
            toolName);

        ToolExecutionPipeline.ProcessedToolCall processed =
            await ProcessAsync(tool, toolName, arguments, turnContext).ConfigureAwait(false);

        return (processed, invoked);
    }
}

/// <summary>Delegates to the real guard while recording every path check the pipeline makes.</summary>
internal sealed class RecordingSanctumGuard(ISanctumGuard inner) : ISanctumGuard
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

internal sealed class InMemorySanctumBreachRepository : ISanctumBreachRepository
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

internal sealed class SingleCampaignRepository(Campaign campaign) : ICampaignRepository
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

internal sealed class RecordOnlyWard : IWard
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
