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
