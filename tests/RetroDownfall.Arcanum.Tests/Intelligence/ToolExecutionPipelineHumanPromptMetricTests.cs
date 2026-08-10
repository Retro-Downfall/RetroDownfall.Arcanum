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
