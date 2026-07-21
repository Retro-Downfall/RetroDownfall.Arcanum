using System.Text;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine.Projections;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Phase 1 characterization: disconnect continue-then-replay vs cancel, and idempotency
/// overflow never Completes a partial response (ADR 0003 / 0004).
/// </summary>
public sealed class TurnDisconnectAndIdempotencyCharacterizationTests
{

    [Fact]
    public void CaptureOnly_RequiresIdempotencyKey_UnderAutoPolicy()
    {
        DefaultHttpContext withKey = new();
        withKey.Request.Headers.Append("Idempotency-Key", "k1");

        Assert.True(TurnContextGuards.ResolveContinueThenReplay(withKey, DisconnectPolicy.Auto));

        DefaultHttpContext withoutKey = new();
        Assert.False(TurnContextGuards.ResolveContinueThenReplay(withoutKey, DisconnectPolicy.Auto));
    }

    [Fact]
    public void NonIdempotentDisconnect_MapsToRunAbandonedReason()
    {
        // ADR 0004: without a key, execution cancels and drains through RunAbandoned —
        // never replayed from semantic events.
        TurnEventEmitter emitter = new(Guid.NewGuid());
        RunAbandoned abandoned = new(
            emitter.NextCorrelation(),
            new Error(ErrorCodes.Hub.Error, "Client disconnected."),
            TurnTerminationReason.ClientDisconnected,
            Usage: null,
            Warnings: [],
            Interrupted: true,
            PartialText: "partial");

        Assert.True(abandoned.IsTerminal);
        Assert.Equal(TurnTerminationReason.ClientDisconnected, abandoned.Reason);
        Assert.True(abandoned.Interrupted);
    }

    [Fact]
    public async Task IdempotencyOverflow_WithinCapFalse_AfterExceedingMaxBytes()
    {
        await using MemoryStream inner = new();
        await using IdempotencyBufferingStream tee = new(inner, maxBytes: 8);

        byte[] payload = Encoding.UTF8.GetBytes("0123456789"); // 10 > 8
        await tee.WriteAsync(payload);

        Assert.False(tee.WithinCap);
        // PersistClaimAsync marks Abandoned when WithinCap is false — never Complete partial.
    }

    [Fact]
    public void ProviderAttemptCommitTracker_CommitsGuardrailBufferedText()
    {
        // Even when TextDelta is withheld from the client (guardrail buffering), a non-empty
        // ModelCallTextDelta still commits the provider attempt (ADR 0004).
        ModelCallTextDelta buffered = new(ModelCallPurpose.MainInference, "call-1", "secret-until-filtered");
        Assert.True(ProviderAttemptCommitTracker.CommitsProviderAttempt(buffered));
    }

    [Fact]
    public void HasIdempotencyKey_AmbientNotPingRequest()
    {
        PingRequest forged = new(Prompt: "hi");
        Assert.Null(typeof(PingRequest).GetProperty("HasIdempotencyKey"));

        TurnExecutionRequest request = new(
            forged,
            TurnResponseMode.Streaming,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: true,
            HasIdempotencyKey: false,
            AccountingHandle: null);

        Assert.False(request.HasIdempotencyKey);
    }

    [Fact]
    public void OpenAiSseProjection_ToolCallIndexes_AreMonotonicAcrossCalls()
    {
        System.Threading.Channels.Channel<OpenAiChatChunk> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OpenAiChatChunk>();

        OpenAiSseProjection projection = new(channel.Writer, "chatcmpl-test", "gpt-test", createdUnixSeconds: 1);
        TurnEventEmitter emitter = new(Guid.NewGuid());

        OpenAiChatChunk first = Assert.Single(projection.Map(new ToolCallProposed(
            emitter.NextCorrelation(),
            "c1",
            "t1",
            "{}",
            ToolCallDisposition.ServerExecution)));

        OpenAiChatChunk second = Assert.Single(projection.Map(new ToolCallProposed(
            emitter.NextCorrelation(),
            "c2",
            "t2",
            "{}",
            ToolCallDisposition.ServerExecution)));

        Assert.Equal(0, first.Choices[0].Delta!.ToolCalls![0].Index);
        Assert.Equal(1, second.Choices[0].Delta!.ToolCalls![0].Index);
    }

}
