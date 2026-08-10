using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Issue #53: the Ward step's decision order is classify → hard deny → auto-approve → interactive,
/// and an auto-approved call still runs every containment check a manually approved call runs.
/// </summary>
public sealed class WardAutoApprovalPipelineTests
{

    private const string AutoApprovedTool = ToolRiskClassifier.ExecuteCommandToolName;

    [Fact]
    public async Task An_allowlisted_tool_runs_without_waiting_for_an_operator()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            AutoApprove(AutoApprovedTool));

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            () =>
            {
                invoked = true;

                return "ran";
            });

        Assert.True(invoked);

        Assert.Equal("ran", processed.ResultText);

        Assert.False(processed.Denied);

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal(1, ward.AutomaticCount);

        Assert.Equal(WardResolutionOrigin.AutoApproved, ward.LastAutomaticOrigin);

    }

    [Fact]
    public async Task An_auto_approved_call_still_emits_warded_and_wardResolved_with_the_automatic_origin()
    {

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            AutoApprove(AutoApprovedTool));

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            static () => "ran");

        IntelligenceEvent warded = Assert.Single(
            processed.WardEvents,
            static e => e.Type == IntelligenceEventType.Warded);

        IntelligenceEvent resolved = Assert.Single(
            processed.WardEvents,
            static e => e.Type == IntelligenceEventType.WardResolved);

        Assert.False(string.IsNullOrWhiteSpace(warded.WardId));

        Assert.Equal(warded.WardId, resolved.WardId);

        Assert.Equal(WardResolutionOrigin.AutoApproved, warded.WardOrigin);

        Assert.Equal(WardResolutionOrigin.AutoApproved, resolved.WardOrigin);

        Assert.True(resolved.WardAllowed);

    }

    [Fact]
    public async Task An_interactive_ward_resolution_is_projected_as_a_human_origin()
    {

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { Enabled = true, ForbiddenArts = [] });

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            static () => "ran");

        IntelligenceEvent resolved = Assert.Single(
            processed.WardEvents,
            static e => e.Type == IntelligenceEventType.WardResolved);

        Assert.Equal(WardResolutionOrigin.Human, resolved.WardOrigin);

    }

    [Fact]
    public async Task An_auto_approved_call_never_raises_a_blocking_approval_request()
    {

        List<ToolExecutionEvent> observed = [];

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            AutoApprove(AutoApprovedTool));

        _ = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            static () => "ran",
            observer: evt =>
            {
                observed.Add(evt);

                return ValueTask.CompletedTask;
            });

        Assert.DoesNotContain(observed, static e => e is ToolApprovalRequestedEvent);

    }

    [Fact]
    public async Task An_auto_approved_call_is_still_blocked_by_Sanctum()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new DenyAllSanctumGuard(),
            AutoApprove(AutoApprovedTool));

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            () =>
            {
                invoked = true;

                return "ran";
            },
            turnContext: SanctumStrictTurnContext());

        Assert.False(invoked);

        Assert.True(processed.Denied);

        Assert.Contains("Sanctum Guard", processed.ResultText, StringComparison.Ordinal);

        Assert.Equal(1, ward.AutomaticCount);

    }

    [Fact]
    public async Task A_tool_outside_the_allowlist_still_waits_for_the_interactive_ward()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            AutoApprove(ToolRiskClassifier.WorkspaceCheckToolName));

        _ = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            static () => "ran");

        Assert.Equal(1, ward.WaitCount);

        Assert.Equal(0, ward.AutomaticCount);

    }

    [Fact]
    public async Task Unattended_auto_deny_wins_over_a_matching_auto_approve_entry()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            AutoApprove(AutoApprovedTool) with { AutoDenyInUnattendedMode = true });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            () =>
            {
                invoked = true;

                return "ran";
            },
            request: new PingRequest("hi", WorkingDirectory: "/tmp", UnattendedMode: true));

        Assert.False(invoked);

        Assert.True(processed.Denied);

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal(0, ward.AutomaticCount);

    }

    [Fact]
    public async Task Auto_approval_is_permitted_in_unattended_mode_when_auto_deny_is_off()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            AutoApprove(AutoApprovedTool) with { AutoDenyInUnattendedMode = false });

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            static () => "ran",
            request: new PingRequest("hi", WorkingDirectory: "/tmp", UnattendedMode: true));

        Assert.False(processed.Denied);

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal(1, ward.AutomaticCount);

    }

    [Fact]
    public async Task Auto_approval_records_a_ward_decision_metric_labelled_only_by_tool_name_and_origin()
    {

        ConcurrentQueue<KeyValuePair<string, object?>[]> measurements = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) =>
                activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {

            if (instrument.Name != "arcanum_ward_decisions_total")
            {
                return;
            }

            measurements.Enqueue(tags.ToArray());

        });

        listener.Start();

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            AutoApprove(AutoApprovedTool));

        _ = await ProcessAsync(
            pipeline,
            AutoApprovedTool,
            static () => "ran");

        KeyValuePair<string, object?>[] recorded = Assert.Single(measurements);

        Assert.Equal(
            ["tool_name", "origin"],
            recorded.Select(static tag => tag.Key));

        Assert.Equal(AutoApprovedTool, recorded[0].Value);

        Assert.Equal("auto_approved", recorded[1].Value);

    }

    private static WardPolicySettings AutoApprove(params string[] tools) =>
        new()
        {
            Enabled = true,
            ForbiddenArts = [],
            AutoApprove = new WardAutoApprovePolicySettings
            {
                Enabled = true,
                Tools = [.. tools],
            },
        };

    private static ToolExecutionPipeline CreatePipeline(
        IWard ward,
        ISanctumGuard sanctumGuard,
        WardPolicySettings wardPolicy) =>
        new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings
            {
                Security = new SecuritySettings { Ward = wardPolicy },
            }),
            ward,
            sanctumGuard,
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

    private static ToolExecutionPipeline.TurnContext SanctumStrictTurnContext() =>
        new()
        {
            Campaign = new Campaign { Id = Guid.NewGuid(), Path = "/tmp" },
            CampaignId = Guid.NewGuid().ToString("D"),
            WorkspaceRoot = "/tmp",
            SanctumEnabled = true,
            SanctumMode = SanctumMode.Strict,
        };

    private static Task<ToolExecutionPipeline.ProcessedToolCall> ProcessAsync(
        ToolExecutionPipeline pipeline,
        string toolName,
        Func<string> implementation,
        ToolExecutionPipeline.TurnContext? turnContext = null,
        PingRequest? request = null,
        Func<ToolExecutionEvent, ValueTask>? observer = null) =>
        pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent($"call-{toolName}", toolName, new Dictionary<string, object?>()),
            request ?? new PingRequest("hi", WorkingDirectory: "/tmp"),
            new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(implementation, toolName)],
            },
            activeSpell: null,
            sessionId: "session-1",
            turnContext ?? new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: false,
            CancellationToken.None,
            observer: observer);

    private sealed class RecordingWard : IWard
    {

        public int WaitCount { get; private set; }

        public int AutomaticCount { get; private set; }

        public WardResolutionOrigin? LastAutomaticOrigin { get; private set; }

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {

            WaitCount++;

            return Task.FromResult(
                new WardResolution(true, null, DateTimeOffset.UtcNow, WardResolutionOrigin.Human));

        }

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) => ResolveStatus.Success;

        public WardResolution RecordAutomaticResolution(
            string wardId,
            bool allowed,
            string? reason,
            WardResolutionOrigin origin)
        {

            AutomaticCount++;

            LastAutomaticOrigin = origin;

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

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
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

    private sealed class DenyAllSanctumGuard : ISanctumGuard
    {

        private static SanctumResult Denied(string toolName) =>
            new()
            {
                Allowed = false,
                DenyReason = "The tool is disabled in this Sanctum.",
                Breach = new SanctumBreach
                {
                    BreachId = Guid.NewGuid().ToString("N"),
                    ToolName = toolName,
                    BreachType = "DisabledTool",
                    Timestamp = DateTimeOffset.UtcNow,
                },
            };

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(Denied(toolName));

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(Denied(toolName));

        public Task<SanctumResult> ValidateToolAsync(
            string campaignId,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(Denied(toolName));

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
