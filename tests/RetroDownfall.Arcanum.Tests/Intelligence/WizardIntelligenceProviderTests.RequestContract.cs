using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_contract_invalid_controls_refuse_direct_turns_without_any_provider_call(bool stream)
    {
        PingRequest original = BaseRequest() with { Prompt = "hello", SkipSpellRouting = true, DisableMcpTools = true };

        PingRequest[] invalid =
        [
            original with { Temperature = float.NaN },
            original with { TopP = 2 },
            original with { PresencePenalty = -3 },
            original with { FrequencyPenalty = 3 },
            original with { MaxOutputTokens = 0 },
            original with { ResponseFormat = "xml" },
            original with { ResponseFormat = "json_schema" },
        ];

        foreach (PingRequest request in invalid)
        {
            ScriptingChatClient chat = new();

            WizardIntelligenceProvider wizard = CreateWizard(chat);

            if (stream)
            {
                List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

                IntelligenceEvent error = Assert.Single(events, e => e.Type == IntelligenceEventType.Error);

                Assert.StartsWith("Validation.", error.Data, StringComparison.Ordinal);

                Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Result);
            }
            else
            {
                Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

                Assert.True(result.IsFailure);

                Assert.StartsWith("Validation.", result.Error.Code, StringComparison.Ordinal);
            }

            Assert.Equal(0, chat.BufferedCallCount);

            Assert.Equal(0, chat.StreamingCallCount);
        }
    }

    [Theory]
    [InlineData("json_schema", false)]
    [InlineData("JSON_SCHEMA", false)]
    [InlineData(" JSON_SCHEMA ", false)]
    [InlineData("json_schema", true)]
    [InlineData("JSON_SCHEMA", true)]
    [InlineData(" JSON_SCHEMA ", true)]
    public async Task Request_contract_schema_spellings_enforce_and_correct_before_releasing_strict_text(string format, bool stream)
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}
            """);

        ScriptingChatClient chat = new();

        if (stream)
        {
            chat.EnqueueStreamTokens("""{"attempt":0}""");
        }
        else
        {
            chat.EnqueueText("""{"attempt":0}""");
        }

        chat.EnqueueText("""{"name":"accepted"}""");

        WizardIntelligenceProvider wizard = CreateWizard(chat);

        PingRequest request = BaseRequest() with
        {
            Prompt = "return structured output",
            ResponseFormat = format,
            ResponseFormatJsonSchema = StrictJsonSchema(schema.RootElement),
            SkipSpellRouting = true,
            DisableMcpTools = true,
        };

        if (stream)
        {
            List<IntelligenceEvent> events = await CollectStreamAsync(wizard, request);

            IntelligenceEvent result = Assert.Single(events, e => e.Type == IntelligenceEventType.Result);

            Assert.Contains("accepted", result.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(events, e => e.Type == IntelligenceEventType.Token && (e.Data?.Contains("attempt", StringComparison.Ordinal) ?? false));
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.Equal("""{"name":"accepted"}""", result.Value.Text);
        }

        Assert.Equal(stream ? 1 : 2, chat.BufferedCallCount);

        Assert.Equal(stream ? 1 : 0, chat.StreamingCallCount);

        Assert.IsType<ChatResponseFormatJson>(chat.LastChatOptions?.ResponseFormat);
    }
}
