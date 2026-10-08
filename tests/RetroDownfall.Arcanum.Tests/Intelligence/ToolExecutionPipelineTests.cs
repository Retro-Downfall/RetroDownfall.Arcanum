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
/// How <see cref="ToolExecutionPipeline.ProcessSingleToolCallAsync"/> classifies the way a tool call ends.
/// </summary>
public sealed class ToolExecutionPipelineTests
{
    /// <summary>
    /// R-274: a cancellation that is not the caller's — a tool's own timeout surfaces as a
    /// <see cref="TaskCanceledException"/> while the turn is still live — is a tool failure. Treating
    /// every <see cref="OperationCanceledException"/> as caller cancellation failed the whole turn
    /// over one tool's internal deadline instead of handing the model a tolerated error result.
    /// </summary>
    [Fact]
    public async Task ProcessSingleToolCallAsync_ToolThrowsTimeoutCancellation_ReturnsToleratedFailure()
    {
        const string toolName = "slow_lookup";

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    string () => throw new TaskCanceledException(
                        "the tool's own request timed out",
                        new TimeoutException()),
                    toolName),
            ],
        };

        ToolExecutionPipeline.ProcessedToolCall processed = await pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent("call-274", toolName, new Dictionary<string, object?>()),
            new PingRequest("hi", WorkingDirectory: "/tmp"),
            chatOptions,
            activeSpell: null,
            sessionId: null,
            turnContext: new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: true,
            cancellationToken: CancellationToken.None);

        Assert.True(processed.Failed);

        Assert.Equal(ToolExecutionPipeline.PublicToolFailureMessage(toolName), processed.ResultText);
    }

    /// <summary>
    /// The caller's own cancellation still propagates: only a cancellation the caller did not ask
    /// for is a tool failure.
    /// </summary>
    [Fact]
    public async Task ProcessSingleToolCallAsync_CallerCancels_Propagates()
    {
        const string toolName = "cancelled_lookup";

        using CancellationTokenSource caller = new();

        ToolExecutionPipeline pipeline = new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance);

        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    string () =>
                    {
                        caller.Cancel();

                        caller.Token.ThrowIfCancellationRequested();

                        return "unreachable";
                    },
                    toolName),
            ],
        };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent("call-274-caller", toolName, new Dictionary<string, object?>()),
            new PingRequest("hi", WorkingDirectory: "/tmp"),
            chatOptions,
            activeSpell: null,
            sessionId: null,
            turnContext: new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: true,
            cancellationToken: caller.Token));
    }

    /// <summary>
    /// R-274, the buffered route that does not tolerate tool failures: every exception propagates
    /// there, a tool's own timeout included, but only a cancellation the caller did not ask for is
    /// counted as an <c>error</c> invocation; the caller's own cancellation propagates uncounted.
    /// </summary>
    [Fact]
    public async Task ProcessSingleToolCallAsync_WithoutTolerance_CountsOnlyANonCallerCancellationAsAnError()
    {
        string timedOutTool = $"timeout_{Guid.NewGuid():N}";
        string cancelledTool = $"cancelled_{Guid.NewGuid():N}";
        using CancellationTokenSource caller = new();
        using ToolOutcomeRecorder outcomes = new();
        ChatOptions chatOptions = new()
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    string () => throw new TaskCanceledException(
                        "the tool's own request timed out",
                        new TimeoutException()),
                    timedOutTool),
                AIFunctionFactory.Create(
                    string () =>
                    {
                        caller.Cancel();

                        caller.Token.ThrowIfCancellationRequested();

                        return "unreachable";
                    },
                    cancelledTool),
            ],
        };
        ToolExecutionPipeline pipeline = CreatePipeline();

        _ = await Assert.ThrowsAsync<TaskCanceledException>(() => pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent("call-274-timeout", timedOutTool, new Dictionary<string, object?>()),
            new PingRequest("hi", WorkingDirectory: "/tmp"),
            chatOptions,
            activeSpell: null,
            sessionId: null,
            turnContext: new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: false,
            cancellationToken: CancellationToken.None));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ProcessSingleToolCallAsync(
            new FunctionCallContent("call-274-cancelled", cancelledTool, new Dictionary<string, object?>()),
            new PingRequest("hi", WorkingDirectory: "/tmp"),
            chatOptions,
            activeSpell: null,
            sessionId: null,
            turnContext: new ToolExecutionPipeline.TurnContext(),
            suppressInvocationFailures: false,
            cancellationToken: caller.Token));

        Assert.Equal(["error"], outcomes.For(timedOutTool));
        Assert.Empty(outcomes.For(cancelledTool));
    }

    /// <summary>
    /// R-274: a timeout inside <c>attach_session_file</c>'s post-processing (the attachment read the
    /// injection makes after the tool returned) is a tolerated tool failure, not the caller's
    /// cancellation; the caller's own cancellation there still propagates.
    /// </summary>
    [Fact]
    public async Task ProcessSingleToolCallAsync_AttachPostProcessTimeout_ReturnsToleratedFailure()
    {
        using CancellationTokenSource caller = new();
        Dictionary<string, object?> arguments = new() { ["logicalName"] = "notes.md" };

        ToolExecutionPipeline.ProcessedToolCall timedOut = await RunWithSessionAsync(() => CreatePipeline(
                new NoOpSessionAttachmentStore(lookupFailure: static () => new TaskCanceledException(
                    "the attachment read timed out",
                    new TimeoutException())))
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("call-274-attach", "attach_session_file", arguments),
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                SessionToolOptions("attach_session_file"),
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                cancellationToken: CancellationToken.None));

        Assert.True(timedOut.Failed);
        Assert.Equal(ToolExecutionPipeline.PublicToolFailureMessage("attach_session_file"), timedOut.ResultText);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunWithSessionAsync(() => CreatePipeline(
                new NoOpSessionAttachmentStore(lookupFailure: () =>
                {
                    caller.Cancel();

                    return new OperationCanceledException(caller.Token);
                }))
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("call-274-attach-caller", "attach_session_file", arguments),
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                SessionToolOptions("attach_session_file"),
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                cancellationToken: caller.Token)));
    }

    /// <summary>
    /// R-274: the same rule for <c>refresh_session_file</c>'s post-processing, which reads the
    /// Session's bound attachments after the tool returned.
    /// </summary>
    [Fact]
    public async Task ProcessSingleToolCallAsync_RefreshPostProcessTimeout_ReturnsToleratedFailure()
    {
        using CancellationTokenSource caller = new();
        Dictionary<string, object?> arguments = new() { ["logicalKey"] = "notes.md" };

        ToolExecutionPipeline.ProcessedToolCall timedOut = await RunWithSessionAsync(() => CreatePipeline(
                new NoOpSessionAttachmentStore(lookupFailure: static () => new TaskCanceledException(
                    "the attachment listing timed out",
                    new TimeoutException())),
                new UnusedAttachmentSourceResolver())
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("call-274-refresh", "refresh_session_file", arguments),
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                SessionToolOptions("refresh_session_file"),
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                cancellationToken: CancellationToken.None));

        Assert.True(timedOut.Failed);
        Assert.Equal(ToolExecutionPipeline.PublicToolFailureMessage("refresh_session_file"), timedOut.ResultText);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunWithSessionAsync(() => CreatePipeline(
                new NoOpSessionAttachmentStore(lookupFailure: () =>
                {
                    caller.Cancel();

                    return new OperationCanceledException(caller.Token);
                }),
                new UnusedAttachmentSourceResolver())
            .ProcessSingleToolCallAsync(
                new FunctionCallContent("call-274-refresh-caller", "refresh_session_file", arguments),
                new PingRequest("hi", WorkingDirectory: "/tmp"),
                SessionToolOptions("refresh_session_file"),
                activeSpell: null,
                sessionId: null,
                turnContext: new ToolExecutionPipeline.TurnContext(),
                suppressInvocationFailures: true,
                cancellationToken: caller.Token)));
    }

    private static ToolExecutionPipeline CreatePipeline(
        NoOpSessionAttachmentStore? store = null,
        IAttachmentSourceResolver? attachmentSourceResolver = null) =>
        new(
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            new FakeWard(),
            new AllowAllSanctumGuard(),
            store ?? new NoOpSessionAttachmentStore(),
            NullLogger<ToolExecutionPipeline>.Instance,
            attachmentSourceResolver: attachmentSourceResolver);

    /// <summary>A Session tool that returns successfully, so its post-processing runs.</summary>
    private static ChatOptions SessionToolOptions(string toolName) =>
        new()
        {
            Tools = [AIFunctionFactory.Create(string () => "{\"status\":\"ok\"}", toolName)],
        };

    /// <summary>Runs <paramref name="call"/> with a current Session bound, as the tool loop does.</summary>
    private static async Task<T> RunWithSessionAsync<T>(Func<Task<T>> call)
    {
        Guid? previous = SessionAttachmentToolAmbient.CurrentSessionId;
        SessionAttachmentToolAmbient.CurrentSessionId = Guid.NewGuid();

        try
        {
            return await call();
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = previous;
        }
    }

    /// <summary>The pipeline needs a resolver to reach the refresh; these tests fail before it is used.</summary>
    private sealed class UnusedAttachmentSourceResolver : IAttachmentSourceResolver
    {
        public Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
            AttachmentSourceClaim claim,
            ReadOnlyMemory<byte> snapshotBytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentSourceMetadata> RevalidateAsync(
            AttachmentSourceMetadata source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The <c>arcanum_tool_invocations_total</c> outcomes recorded for each tool, read from the
    /// process-wide meter; tests filter by a tool name unique to them, so concurrent classes driving
    /// the pipeline cannot be miscounted.
    /// </summary>
    private sealed class ToolOutcomeRecorder : IDisposable
    {
        private readonly ConcurrentQueue<(string Tool, string Outcome)> _outcomes = new();

        private readonly MeterListener _listener = new();

        public ToolOutcomeRecorder()
        {
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Name == "arcanum_tool_invocations_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? tool = null;
                string? outcome = null;

                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == "tool_name")
                    {
                        tool = tag.Value as string;
                    }
                    else if (tag.Key == "outcome")
                    {
                        outcome = tag.Value as string;
                    }
                }

                if (tool is not null && outcome is not null)
                {
                    _outcomes.Enqueue((tool, outcome));
                }
            });

            _listener.Start();
        }

        public IReadOnlyList<string> For(string tool) =>
            [.. _outcomes.Where(entry => entry.Tool == tool).Select(static entry => entry.Outcome)];

        public void Dispose() => _listener.Dispose();
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
