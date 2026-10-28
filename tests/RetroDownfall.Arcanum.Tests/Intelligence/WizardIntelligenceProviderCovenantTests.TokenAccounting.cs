using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderCovenantTests
{
    [Fact]
    public async Task Ordinary_dispatch_and_supported_preview_agree_on_actual_covenant_partition()
    {
        CapturingChatClient chat = Scripted();

        FakeInferenceAuditLogger audit = new();

        WizardIntelligenceProvider wizard = Wizard(chat, Gate(confirmed: 2, proposed: 1), audit);

        Result<PromptTurnResult> actual = await wizard.ExecutePromptAsync(Request(),
            InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId), CancellationToken.None,
            new InferenceAuditContext { RequestType = "token-partition" });

        Assert.True(actual.IsSuccess, actual.Error.Message);

        Result<ContextPreviewResult> preview = await wizard.PreviewContextAsync(new ContextPreviewRequest(
            Prompt: "hello", Model: ModelName, CampaignId: CovenantTask6Fixture.CampaignId, NoRetrieval: true),
            InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId), CancellationToken.None);

        Assert.True(preview.IsSuccess, preview.Error.Message);

        ContextTokenBreakdown dispatched = LastBreakdown(audit);

        Assert.Equal(Assert.Single(preview.Value.Sources, static source => source.Source == ContextTokenSource.CovenantConfirmed).TokenCount,
            dispatched.Source(ContextTokenSource.CovenantConfirmed).TokenCount);

        Assert.Equal(Assert.Single(preview.Value.Sources, static source => source.Source == ContextTokenSource.CovenantProposed).TokenCount,
            dispatched.Source(ContextTokenSource.CovenantProposed).TokenCount);

        Assert.Single(chat.SystemPrompts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_correction_preserves_attribution_for_each_physical_dispatch(bool streamed)
    {
        CapturingChatClient chat = new();

        chat.EnqueueText("{}");

        chat.EnqueueText("{\"name\":\"accepted\"}");

        FakeInferenceAuditLogger audit = new();

        WizardIntelligenceProvider wizard = Wizard(chat, Gate(confirmed: 2, proposed: 1), audit);

        using JsonDocument schema = JsonDocument.Parse("{\"strict\":true,\"schema\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}}");

        PingRequest request = Request() with { ResponseFormat = "json_schema", ResponseFormatJsonSchema = schema.RootElement.Clone() };

        if (streamed)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(request,
                InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId), CancellationToken.None,
                new InferenceAuditContext { RequestType = "token-partition-retry" }))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result && frame.Message == "{\"name\":\"accepted\"}");

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request,
                InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId), CancellationToken.None,
                new InferenceAuditContext { RequestType = "token-partition-retry" });

            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.Equal("{\"name\":\"accepted\"}", result.Value.Text);
        }

        Assert.Equal(2, chat.SystemPrompts.Count);

        Assert.All(chat.SystemPrompts, static prompt =>
        {
            Assert.Contains("confirmed.0", prompt, StringComparison.Ordinal);

            Assert.Contains("proposed.0", prompt, StringComparison.Ordinal);
        });

        List<ContextTokenBreakdown> measured = Assert.Single(audit.Records).ContextBreakdowns!;

        Assert.Equal(2, measured.Count);

        Assert.All(measured, static breakdown =>
        {
            Assert.True(breakdown.Source(ContextTokenSource.CovenantConfirmed).TokenCount > 0);

            Assert.True(breakdown.Source(ContextTokenSource.CovenantProposed).TokenCount > 0);

            Assert.Equal(breakdown.TotalTokens, breakdown.Components.Sum(static component => component.Estimate.TokenCount));
        });
    }

    [Theory]
    [InlineData(false, ContextTokenSource.CovenantConfirmed)]
    [InlineData(true, ContextTokenSource.CovenantConfirmed)]
    [InlineData(false, ContextTokenSource.CovenantProposed)]
    [InlineData(true, ContextTokenSource.CovenantProposed)]
    public async Task Ordinary_dispatch_accounts_actual_confirmed_and_proposed_content_without_cache_preparation(bool streamed, ContextTokenSource source)
    {
        CapturingChatClient chat = Scripted();

        FakeInferenceAuditLogger audit = new();

        WizardIntelligenceProvider wizard = Wizard(chat, Gate(confirmed: 2, proposed: 1), audit);

        List<IntelligenceEvent> frames = [];

        if (streamed)
        {
            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(Request(),
                InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId), CancellationToken.None,
                new InferenceAuditContext { RequestType = "token-partition" }))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);

            Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(Request(),
                InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId), CancellationToken.None,
                new InferenceAuditContext { RequestType = "token-partition" });

            Assert.True(result.IsSuccess, result.Error.Message);
        }

        string dispatched = Assert.Single(chat.SystemPrompts);

        Assert.Contains("confirmed.0", dispatched, StringComparison.Ordinal);

        Assert.Contains("proposed.0", dispatched, StringComparison.Ordinal);

        ContextTokenBreakdown actual = LastBreakdown(audit);

        Assert.Equal(actual.InputTokens + actual.ReservedTokens, actual.TotalTokens);

        Assert.Equal(actual.TotalTokens, actual.Components.Sum(static component => component.Estimate.TokenCount));

        Assert.True(actual.Source(source).TokenCount > 0, $"Dispatched {source} content was charged as zero tokens.");

        if (streamed)
        {
            ContextTokenBreakdown emitted = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Context).ContextBreakdown!;

            Assert.Equal(actual.Source(ContextTokenSource.CovenantConfirmed), emitted.Source(ContextTokenSource.CovenantConfirmed));

            Assert.Equal(actual.Source(ContextTokenSource.CovenantProposed), emitted.Source(ContextTokenSource.CovenantProposed));
        }
    }
}
