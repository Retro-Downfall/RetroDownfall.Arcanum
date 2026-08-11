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
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// <c>arcanum_tool_invocations_total</c> lives on the process-wide <c>"Arcanum"</c> meter, and a
/// <see cref="MeterListener"/> observes every measurement recorded anywhere in the process. The
/// canonical-tool-name test below cannot tag its measurement with a unique marker — the whole point
/// is that the pipeline records the literal <c>send_commlink_alert</c> — so its
/// <c>Assert.Single</c> is only correct while no other class is driving
/// <c>ToolExecutionPipeline</c> concurrently. The <c>Telemetry</c> collection is the
/// <c>DisableParallelization</c> guarantee that makes that true.
/// </summary>
[Collection("Telemetry")]
public sealed class ToolExecutionPipelineHumanPromptMetricTests
{

    [Fact]
    public async Task ProcessSingleToolCall_HumanPromptTimeout_RecordsErrorOutcome()
    {

        string toolMarker = $"ask_human_{Guid.NewGuid():N}";

        ConcurrentQueue<string> outcomes = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name != "arcanum_tool_invocations_total")
            {
                return;
            }

            string? toolName = null;

            string? outcome = null;

            foreach (KeyValuePair<string, object?> tag in tags)
            {

                if (tag.Key == "tool_name" && tag.Value is string tn)
                {
                    toolName = tn;
                }

                if (tag.Key == "outcome" && tag.Value is string o)
                {
                    outcome = o;
                }

            }

            if (string.Equals(toolName, toolMarker, StringComparison.Ordinal) && outcome is not null)
            {
                outcomes.Enqueue(outcome);
            }

        });

        listener.Start();

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        FunctionCallContent fcc = new(toolMarker, toolMarker, new Dictionary<string, object?>());

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    () =>
                    {
                        throw new HumanPromptTimeoutException();
#pragma warning disable CS0162
                        return "unreachable";
#pragma warning restore CS0162
                    },
                    toolMarker),
            ],
        };

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
            .ProcessSingleToolCallAsync(
                fcc,
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                chatOptions,
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                cancellationToken: CancellationToken.None);

        Assert.True(processed.Failed);

        Assert.Equal(HumanPromptTimeoutException.DefaultMessage, processed.ResultText);

        string recorded = Assert.Single(outcomes);

        Assert.Equal("error", recorded);

    }

    [Fact]
    public async Task ProcessSingleToolCall_UseCommlink_RecordsCanonicalSendCommlinkAlertMetric()
    {

        ConcurrentQueue<string> toolNames = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name != "arcanum_tool_invocations_total")
            {
                return;
            }

            foreach (KeyValuePair<string, object?> tag in tags)
            {

                if (tag.Key == "tool_name" && tag.Value is string tn)
                {
                    toolNames.Enqueue(tn);
                }

            }

        });

        listener.Start();

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        FunctionCallContent fcc = new("call_1", "send_commlink_alert", new Dictionary<string, object?>());

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(() => "sent", "send_commlink_alert"),
            ],
        };

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
            .ProcessSingleToolCallAsync(
                fcc,
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                chatOptions,
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                cancellationToken: CancellationToken.None);

        Assert.False(processed.Failed);

        Assert.Equal("send_commlink_alert", processed.ToolName);

        string recorded = Assert.Single(toolNames);

        Assert.Equal("send_commlink_alert", recorded);

    }

    /// <summary>
    /// A model can name a tool that was never advertised. Nothing is invoked in that case, so the
    /// call must not be counted as a success, and the model's arbitrary string must not become an
    /// unbounded <c>tool_name</c> label — the metric's documented "bounded by construction"
    /// invariant depends on collapsing it to a fixed sentinel.
    /// </summary>
    [Fact]
    public async Task ProcessSingleToolCall_UnregisteredToolName_RecordsSentinelLabelAndErrorOutcome()
    {

        ConcurrentQueue<(string ToolName, string Outcome)> measurements = new();

        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name != "arcanum_tool_invocations_total")
            {
                return;
            }

            string? toolName = null;

            string? outcome = null;

            foreach (KeyValuePair<string, object?> tag in tags)
            {

                if (tag.Key == "tool_name" && tag.Value is string tn)
                {
                    toolName = tn;
                }

                if (tag.Key == "outcome" && tag.Value is string o)
                {
                    outcome = o;
                }

            }

            if (toolName is not null && outcome is not null)
            {
                measurements.Enqueue((toolName, outcome));
            }

        });

        listener.Start();

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        string hallucinated = $"totally_made_up_{Guid.NewGuid():N}";

        FunctionCallContent fcc = new("call_1", hallucinated, new Dictionary<string, object?>());

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(() => "sent", "send_commlink_alert"),
            ],
        };

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline
            .ProcessSingleToolCallAsync(
                fcc,
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                chatOptions,
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                cancellationToken: CancellationToken.None);

        Assert.Contains("No local tool registered", processed.ResultText, StringComparison.Ordinal);

        (string ToolName, string Outcome) recorded = Assert.Single(measurements);

        Assert.Equal(ToolExecutionPipeline.UnregisteredToolMetricLabel, recorded.ToolName);

        Assert.Equal("error", recorded.Outcome);

    }

    private sealed class FakeWard : IWard
    {

        public Task<WardResolution> WardAsync(
            string wardId,
            string toolName,
            JsonDocument? arguments,
            string? sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WardResolution(true, null, DateTimeOffset.UtcNow));

        public ResolveStatus Resolve(string wardId, bool allow, string? reason) => ResolveStatus.Success;

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
