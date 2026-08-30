using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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
    public async Task Workspace_check_emits_a_record_only_Ward_with_host_owned_execution_risk_disclosure()
    {

        CapturingWard ward = new();
        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings
            {
                Security = new SecuritySettings
                {
                    Ward = new WardPolicySettings
                    {
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

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline.ProcessSingleToolCallAsync(
            call,
            new PingRequest("check"),
            new ChatOptions { Tools = [] },
            activeSpell: null,
            sessionId: null,
            new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: true,
            CancellationToken.None);

        IntelligenceEvent warded = Assert.Single(
            processed.WardEvents,
            static evt => evt.Type == IntelligenceEventType.Warded);

        IntelligenceEvent resolved = Assert.Single(
            processed.WardEvents,
            static evt => evt.Type == IntelligenceEventType.WardResolved);

        Assert.Equal(WardResolutionOrigin.Ungated, warded.WardOrigin);

        Assert.Equal(WardResolutionOrigin.Ungated, resolved.WardOrigin);

        Assert.NotNull(warded.WardArguments);

        string argumentsJson = warded.WardArguments.Value.GetRawText();

        Assert.Contains(
            "workspace-authored code",
            argumentsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "read-only",
            argumentsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "writable build",
            argumentsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "network",
            argumentsJson,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, ward.WardAsyncCallCount);

        Assert.Equal(1, ward.RecordAutomaticResolutionCallCount);
    }

    private sealed class CapturingWard : IWard
    {

        public int WardAsyncCallCount { get; private set; }

        public int RecordAutomaticResolutionCallCount { get; private set; }

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {

            WardAsyncCallCount++;

            return Task.FromResult(new WardResolution(true, null, DateTimeOffset.UtcNow));
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
            WardResolutionOrigin origin)
        {

            RecordAutomaticResolutionCallCount++;

            return new WardResolution(allowed, reason, DateTimeOffset.UtcNow, origin);
        }

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
