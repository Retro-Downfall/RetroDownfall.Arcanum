using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, "stop", "length")]
    [InlineData(true, "stop", "length")]
    [InlineData(false, "length", "stop")]
    [InlineData(true, "length", "stop")]
    public async Task OutputLimitNotice_structured_correction_supplies_the_selected_final_finish_reason(
        bool streaming,
        string initialFinishReason,
        string finalFinishReason)
    {
        const string correctedAnswer = "{\"ok\":true}";

        ScriptingChatClient chat = new();

        EnqueueOutputLimitResponse(chat, streaming,
            initialFinishReason == "length" ? string.Empty : "invalid JSON",
            new ChatFinishReason(initialFinishReason));

        // The strict schema correction path makes a buffered request in both public lanes.
        EnqueueOutputLimitResponse(chat, streaming: false, correctedAnswer, new ChatFinishReason(finalFinishReason));

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": { "ok": { "type": "boolean" } },
              "required": ["ok"],
              "additionalProperties": false
            }
            """);

        FakeGrimoireRepository grimoire = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, grimoire: grimoire);

        OutputLimitObservation observed = await ObserveOutputLimitTurnAsync(wizard, streaming,
            OutputLimitRequest() with
            {
                ResponseFormat = "json_schema",
                ResponseFormatJsonSchema = StrictJsonSchema(schema.RootElement),
            });

        Assert.True(observed.Success, observed.WireJson);

        Assert.Equal(correctedAnswer, observed.Text);

        Assert.Equal(correctedAnswer, grimoire.LastFinalizedContent);

        Assert.Equal(finalFinishReason, observed.FinishReason);

        Assert.DoesNotContain(observed.Notices, static notice => (int)notice.Code == 3);

        Assert.DoesNotContain(observed.Events, static frame => frame.Notices.Any(notice => (int)notice.Code == 3));

        Assert.Equal(finalFinishReason == "length" ? 1 : 0,
            observed.Notices.Count(static notice => notice.Code == InferenceNoticeCode.OutputLimitWithPartialAnswer));

        if (streaming)
        {
            Assert.Equal(correctedAnswer, string.Concat(observed.Events
                .Where(static frame => frame.Type == IntelligenceEventType.Token)
                .Select(static frame => frame.Data)));
        }

        Assert.Equal(streaming ? 1 : 0, chat.StreamingCallCount);

        Assert.Equal(streaming ? 1 : 2, chat.BufferedCallCount);
    }
}
