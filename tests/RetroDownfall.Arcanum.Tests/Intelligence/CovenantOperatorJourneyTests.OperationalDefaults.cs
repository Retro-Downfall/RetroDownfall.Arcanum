using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class CovenantOperatorJourneyTests
{
    [SkippableFact]
    public async Task A_confirmed_sampling_default_survives_new_sessions_and_stays_inside_its_campaign()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "sampling-a");

        await SeedCampaignAsync(CampaignB, "sampling-b");

        Result<CovenantMutationResultDto> written = await SetCampaignAsync(
            CampaignA,
            "defaults.inference.temperature",
            "0.25",
            expectedRevision: 0);

        Assert.True(written.IsSuccess, written.IsFailure ? written.Error.Message : null);

        (Guid firstSession, ChatOptions first) = await RunSamplingSessionAsync(CampaignA);

        (Guid laterSession, ChatOptions later) = await RunSamplingSessionAsync(CampaignA);

        (_, ChatOptions otherCampaign) = await RunSamplingSessionAsync(CampaignB);

        Assert.NotEqual(firstSession, laterSession);

        Assert.Equal(0.25f, first.Temperature);

        Assert.Equal(0.25f, later.Temperature);

        Assert.Null(otherCampaign.Temperature);
    }

    private async Task<(Guid SessionId, ChatOptions Options)> RunSamplingSessionAsync(Guid campaignId)
    {
        Guid sessionId = await SeedSessionAsync(campaignId);

        RecordingChatClient chat = new(AssistantAnswer);

        Result<PromptTurnResult> turn = await Wizard(chat, Gate(), new CovenantToolCapabilityRegistry())
            .ExecutePromptAsync(
                new PingRequest(
                    Prompt: OperatorPrompt,
                    Model: ModelName,
                    WorkingDirectory: string.Empty,
                    SessionId: sessionId,
                    DisableMcpTools: true,
                    SkipSpellRouting: true),
                Invocation(campaignId),
                CancellationToken.None);

        Assert.True(turn.IsSuccess, turn.IsFailure ? turn.Error.Message : null);

        return (sessionId, Assert.IsType<ChatOptions>(chat.Options));
    }
}
