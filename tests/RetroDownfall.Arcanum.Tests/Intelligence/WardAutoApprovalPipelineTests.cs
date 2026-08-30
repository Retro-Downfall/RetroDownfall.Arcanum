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
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Issues #216 and #217: every ordinary tool call receives a record-only Ward pair without entering
/// the retired classifier, prompt, or auto-denial path.
///
/// The metric test asserts <c>Assert.Single</c> over <c>arcanum_ward_decisions_total</c>, which is a
/// single process-wide instrument on the shared <c>"Arcanum"</c> meter — every ward decision recorded
/// by any concurrently running class lands in the same listener. The <c>Telemetry</c> collection is
/// the <c>DisableParallelization</c> guarantee that assertion depends on.
/// </summary>
[Collection("Telemetry")]
public sealed class WardRecordPipelineTests
{
    [Theory]
    [InlineData("read_file_chunk")]
    [InlineData("read_saga")]
    [InlineData("delegate_task")]
    [InlineData("web_search")]
    [InlineData("write_file")]
    [InlineData("execute_command")]
    public async Task Every_ordinary_tool_records_an_ungated_ward_pair_without_blocking(string toolName)
    {

        List<ToolExecutionEvent> observed = [];

        WardGate ward = new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            toolName,
            () =>
            {
                invoked = true;

                return "ran";
            },
            observer: evt =>
            {
                observed.Add(evt);

                return ValueTask.CompletedTask;
            },
            argumentsSnapshot: """{"scope":"fixture"}""");

        Assert.True(invoked);

        Assert.Equal("ran", processed.ResultText);

        Assert.False(processed.Denied);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        IntelligenceEvent warded = processed.WardEvents[0];

        IntelligenceEvent resolved = processed.WardEvents[1];

        Assert.False(string.IsNullOrWhiteSpace(warded.WardId));

        Assert.Equal(warded.WardId, resolved.WardId);

        Assert.Equal(toolName, warded.WardToolName);

        Assert.Equal(toolName, resolved.WardToolName);

        JsonElement arguments = Assert.IsType<JsonElement>(warded.WardArguments);

        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);

        Assert.Equal("fixture", arguments.GetProperty("scope").GetString());

        Assert.Equal(WardResolutionOrigin.Ungated, warded.WardOrigin);

        Assert.Equal(WardResolutionOrigin.Ungated, resolved.WardOrigin);

        Assert.True(resolved.WardAllowed);

        Assert.DoesNotContain(observed, static evt => evt is ToolApprovalRequestedEvent);

        Assert.Empty(ward.GetActiveWards());

    }

    [Fact]
    public async Task Unattended_write_file_call_executes_when_listed_as_a_forbidden_art()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new AllowAllSanctumGuard(),
            new WardPolicySettings
            {
                ForbiddenArts = ["write_file"],
                UnattendedMode = true,
            });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            "write_file",
            () =>
            {
                invoked = true;

                return "ran";
            },
            request: new PingRequest("hi", WorkingDirectory: "/tmp", UnattendedMode: true));

        Assert.True(invoked);

        Assert.Equal("ran", processed.ResultText);

        Assert.False(processed.Denied);

        Assert.Equal(0, ward.WaitCount);

        Assert.Equal(1, ward.AutomaticCount);

        Assert.Equal(WardResolutionOrigin.Ungated, ward.LastAutomaticOrigin);

    }

    [Fact]
    public async Task A_configured_forbidden_art_is_recorded_then_blocked_by_Sanctum()
    {

        RecordingWard ward = new();

        ToolExecutionPipeline pipeline = CreatePipeline(
            ward,
            new DenyAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = ["execute_command"] });

        bool invoked = false;

        ToolExecutionPipeline.ProcessedToolCall processed = await ProcessAsync(
            pipeline,
            "execute_command",
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

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static evt => evt.Type));

        Assert.All(
            processed.WardEvents,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

    }

    [Theory]
    [InlineData("web_search", "web_search", true)]
    [InlineData("execute_command", "execute_command", true)]
    [InlineData("model_supplied_unknown_tool", "unregistered", false)]
    public async Task Every_ordinary_tool_records_an_ungated_ward_decision_metric(
        string toolName,
        string metricToolName,
        bool registerTool)
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
            new WardPolicySettings { ForbiddenArts = [] });

        _ = await ProcessAsync(
            pipeline,
            toolName,
            static () => "ran",
            registerTool: registerTool);

        KeyValuePair<string, object?>[] recorded = Assert.Single(measurements);

        Assert.Equal(
            ["tool_name", "origin"],
            recorded.Select(static tag => tag.Key));

        Assert.Equal(metricToolName, recorded[0].Value);

        Assert.Equal("ungated", recorded[1].Value);

    }

    /// <summary>
    /// On the buffered path the Ward frames are accumulated rather than emitted live. If the tool
    /// then throws under the tolerant-failure policy — an MCP transport fault, a workspace IO error
    /// — the client must still learn that the call was recorded and how it was resolved, not merely
    /// that something failed.
    /// </summary>
    [Fact]
    public async Task A_tolerated_invocation_failure_still_reports_ungated_record_frames()
    {

        ToolExecutionPipeline pipeline = CreatePipeline(
            new RecordingWard(),
            new AllowAllSanctumGuard(),
            new WardPolicySettings { ForbiddenArts = [] });

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("call-write_file", "write_file", new Dictionary<string, object?>()),
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                new ChatOptions
                {
                    Tools =
                    [
                        AIFunctionFactory.Create(
                            () =>
                            {
                                throw new InvalidOperationException("mcp transport fault");
#pragma warning disable CS0162
                                return "unreachable";
#pragma warning restore CS0162
                            },
                            "write_file"),
                    ],
                },
                activeSpell: null,
                sessionId: "session-1",
                new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                CancellationToken.None);

        Assert.True(processed.Failed);

        Assert.Equal(
            [IntelligenceEventType.Warded, IntelligenceEventType.WardResolved],
            processed.WardEvents.Select(static e => e.Type));

        Assert.All(
            processed.WardEvents,
            static evt => Assert.Equal(WardResolutionOrigin.Ungated, evt.WardOrigin));

    }

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
        Func<ToolExecutionEvent, ValueTask>? observer = null,
        string? argumentsSnapshot = null,
        bool registerTool = true) =>
        pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent($"call-{toolName}", toolName, new Dictionary<string, object?>()),
            request ?? new PingRequest("hi", WorkingDirectory: "/tmp"),
            new ChatOptions
            {
                Tools = registerTool
                    ? [AIFunctionFactory.Create(implementation, toolName)]
                    : [],
            },
            activeSpell: null,
            sessionId: "session-1",
            turnContext ?? new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: false,
            CancellationToken.None,
            observer: observer,
            argumentsSnapshot: argumentsSnapshot);

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
