using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderCovenantTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_sampling_defaults_reach_buffered_and_streaming_provider_options(bool streaming)
    {
        CapturingChatClient chat = Scripted();

        PingRequest request = Request() with
        {
            DisableAllTools = true,
            MaxOutputTokens = 80,
            Seed = 17,
            PresencePenalty = 0.3f,
            FrequencyPenalty = -0.2f,
            ResponseFormat = "text"
        };

        await RunSamplingTurnAsync(chat, SamplingPlan(), request, streaming);

        ChatOptions options = Assert.Single(chat.Options);

        Assert.Equal(0.25f, options.Temperature);

        Assert.Equal(0.75f, options.TopP);

        Assert.Equal(80, options.MaxOutputTokens);

        Assert.Equal(17, options.Seed);

        Assert.Equal(0.3f, options.PresencePenalty);

        Assert.Equal(-0.2f, options.FrequencyPenalty);

        Assert.Equal(ChatToolMode.None, options.ToolMode);

        Assert.True(options.Tools is null or { Count: 0 });

        Assert.Equal(ChatResponseFormat.Text, options.ResponseFormat);

        Assert.Null(request.Temperature);

        Assert.Null(request.TopP);
    }

    [Fact]
    public async Task Explicit_sampling_values_win_over_confirmed_defaults()
    {
        CapturingChatClient chat = Scripted();

        await RunSamplingTurnAsync(chat, SamplingPlan(), Request() with { Temperature = 0.6f, TopP = 0.9f });

        ChatOptions options = Assert.Single(chat.Options);

        Assert.Equal(0.6f, options.Temperature);

        Assert.Equal(0.9f, options.TopP);
    }

    [Fact]
    public async Task Proposed_sampling_values_remain_data_and_do_not_change_provider_options()
    {
        CapturingChatClient chat = Scripted();

        await RunSamplingTurnAsync(chat, SamplingPlan(proposed: true), Request());

        ChatOptions options = Assert.Single(chat.Options);

        Assert.Null(options.Temperature);

        Assert.Null(options.TopP);
    }

    [Fact]
    public async Task Withheld_confirmed_sampling_defaults_cannot_change_provider_options()
    {
        CapturingChatClient chat = Scripted();

        WizardIntelligenceProvider wizard = Wizard(chat, Gate(SamplingPlan(withFillers: true)), contextWindowLimit: 2_300);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            Request(),
            InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        ChatOptions options = Assert.Single(chat.Options);

        Assert.Null(options.Temperature);

        Assert.Null(options.TopP);

        Assert.DoesNotContain("The Covenant", Assert.Single(chat.SystemPrompts), StringComparison.Ordinal);
    }

    private async Task RunSamplingTurnAsync(
        CapturingChatClient chat,
        CovenantTurnPlan plan,
        PingRequest request,
        bool streaming = false)
    {
        WizardIntelligenceProvider wizard = Wizard(chat, Gate(plan));

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(
                request,
                InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId),
                CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                request,
                InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }
    }

    private static CovenantTurnPlan SamplingPlan(bool proposed = false, bool withFillers = false)
    {
        List<CovenantSnapshotCandidate> candidates = withFillers
            ? [.. CovenantCompositionFixture.Plan(16, 0).Snapshot.Candidates]
            : [];

        string[] keys = ["defaults.inference.temperature", "defaults.inference.top-p"];

        string[] values = ["0.25", "0.75"];

        for (int index = 0; index < keys.Length; index++)
        {
            CovenantCompiledContent content = new CovenantCompiler().Compile(keys[index], values[index]);

            candidates.Add(new CovenantSnapshotCandidate(
                (ulong)(100 + index),
                CovenantTask6Fixture.GuidFor(700 + index),
                CovenantTask6Fixture.GuidFor(710 + index),
                new CovenantKey(keys[index]),
                proposed ? CovenantScope.Campaign : CovenantScope.Global,
                proposed ? CovenantTask6Fixture.CampaignId : null,
                proposed ? CovenantLane.Proposed : CovenantLane.Confirmed,
                CovenantOperation.Set,
                proposed ? CovenantOrigin.AgentProposed : CovenantOrigin.Operator,
                1,
                null,
                (uint)content.CompilerPolicyVersion,
                (uint)content.RendererPolicyVersion,
                content.AuthoredHash,
                content.FragmentHash,
                0,
                CovenantTask6Fixture.D(1),
                [.. content.FragmentUtf8],
                CovenantSnapshotCandidateIntegrity.Verified));
        }

        return new CovenantLinker().Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, [.. candidates])).Value;
    }
}
