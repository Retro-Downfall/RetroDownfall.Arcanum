using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ToolExecutionObserverTimingTests
{

    [Fact]
    public async Task ProcessSingleToolCall_EmitsApprovalRequested_BeforeWardWaitCompletes()
    {
        List<ToolExecutionEvent> observed = [];
        TaskCompletionSource wardEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource wardRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings
            {
                Security = new SecuritySettings
                {
                    Ward = new WardPolicySettings
                    {
                        Enabled = true,
                        ForbiddenArts = ["write_file"],
                    },
                },
            }),
            new GatingWard(wardEntered, wardRelease),
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        FunctionCallContent fcc = new("call-1", "write_file", new Dictionary<string, object?>
        {
            ["path"] = "x.txt",
            ["content"] = "hi",
        });

        ChatOptions options = new() { Tools = [] };

        ToolExecutionPipeline.TurnContext turnContext = new()
        {
            CampaignRequiresWard = true,
        };

        Task<ToolExecutionPipeline.ProcessedToolCall> invoke = pipeline.ProcessSingleToolCallAsync(
            fcc,
            new PingRequest("hi"),
            options,
            activeSpell: null,
            sessionId: null,
            turnContext,
            suppressInvocationFailures: true,
            CancellationToken.None,
            observer: evt =>
            {
                observed.Add(evt);

                return ValueTask.CompletedTask;
            });

        await wardEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(observed, e => e is ToolApprovalRequestedEvent);

        wardRelease.SetResult();

        _ = await invoke.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Workspace_check_Ward_receives_host_owned_execution_risk_disclosure()
    {

        CapturingDenyWard ward = new();
        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings
            {
                Security = new SecuritySettings
                {
                    Ward = new WardPolicySettings
                    {
                        Enabled = true,
                        ForbiddenArts = [],
                    },
                },
            }),
            ward,
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);
        FunctionCallContent call = new(
            "call-check",
            ToolRiskClassifier.WorkspaceCheckToolName,
            new Dictionary<string, object?>
            {
                ["profile"] = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
            });

        _ = await pipeline.ProcessSingleToolCallAsync(
            call,
            new PingRequest("check"),
            new ChatOptions { Tools = [] },
            activeSpell: null,
            sessionId: null,
            new ToolExecutionPipeline.TurnContext
            {
                CampaignRequiresWard = false,
            },
            suppressInvocationFailures: true,
            CancellationToken.None);

        Assert.Contains(
            "workspace-authored code",
            ward.ArgumentsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "read-only",
            ward.ArgumentsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "writable build",
            ward.ArgumentsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "network",
            ward.ArgumentsJson,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GatingWard(TaskCompletionSource entered, TaskCompletionSource release) : IWard
    {

        public async Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();

            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new WardResolution(Allowed: true, Reason: null, DateTimeOffset.UtcNow);
        }

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) => ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin) =>
            new(allowed, reason, DateTimeOffset.UtcNow, origin);

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];

    }

    private sealed class CapturingDenyWard : IWard
    {

        public string ArgumentsJson { get; private set; } = string.Empty;

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {

            ArgumentsJson = arguments?.RootElement.GetRawText()
                ?? string.Empty;

            return Task.FromResult(
                new WardResolution(
                    Allowed: false,
                    Reason: "denied",
                    DateTimeOffset.UtcNow));
        }

        public ResolveStatus Resolve(
            string wardId,
            bool allow,
            string? reason) =>
            ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin) =>
            new(allowed, reason, DateTimeOffset.UtcNow, origin);

        public IReadOnlyList<ActiveWard> GetActiveWards() => [];
    }

    private sealed class AllowAllSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

}
