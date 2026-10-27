using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class InferenceRequestEndpointContractTests(ArcanumWebApplicationFactory factory)
{
    [Theory]
    [InlineData("\"temperature\":7", "temperature")]
    [InlineData("\"topP\":3", "topP")]
    [InlineData("\"presencePenalty\":-9", "presencePenalty")]
    [InlineData("\"frequencyPenalty\":3", "frequencyPenalty")]
    [InlineData("\"maxOutputTokens\":0", "maxOutputTokens")]
    [InlineData("\"responseFormat\":\"xml\"", "responseFormat")]
    [InlineData("\"responseFormat\":\"json_schema\"", "responseFormat")]
    public async Task Native_invalid_controls_return_400_before_buffered_or_streamed_inference(string fields, string field)
    {
        using HttpClient client = factory.CreateAuthenticatedClient();

        int bufferedBefore = factory.FakeIntelligence.ExecutePromptCallCount;

        int streamedBefore = factory.FakeIntelligence.StreamPromptCallCount;

        foreach (string route in new[] { "/api/intelligence/ping", "/api/intelligence/ping-stream" })
        {
            using StringContent content = new($$"""{"prompt":"hello",{{fields}}}""", Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.PostAsync(route, content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());

            Assert.Contains(field, body.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        Assert.Equal(bufferedBefore, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(streamedBefore, factory.FakeIntelligence.StreamPromptCallCount);
    }

    [Theory]
    [InlineData("\"temperature\":7", "temperature")]
    [InlineData("\"top_p\":3", "top_p")]
    [InlineData("\"presence_penalty\":-9", "presence_penalty")]
    [InlineData("\"frequency_penalty\":3", "frequency_penalty")]
    [InlineData("\"max_tokens\":0", "max_tokens")]
    [InlineData("\"max_completion_tokens\":-5", "max_completion_tokens")]
    [InlineData("\"max_tokens\":-5,\"max_completion_tokens\":12", "max_tokens")]
    [InlineData("\"response_format\":{\"type\":\"xml\"}", "response_format")]
    [InlineData("\"response_format\":{}", "response_format")]
    [InlineData("\"response_format\":{\"type\":\"\"}", "response_format")]
    [InlineData("\"response_format\":{\"type\":\"json_schema\"}", "response_format")]
    [InlineData("\"response_format\":{\"type\":\"json_schema\",\"json_schema\":{\"schema\":null}}", "response_format")]
    public async Task Openai_invalid_controls_have_wire_parameter_in_live_and_batch_validation(string fields, string parameter)
    {
        using HttpClient client = factory.CreateAuthenticatedClient();

        int bufferedBefore = factory.FakeIntelligence.ExecutePromptCallCount;

        int streamedBefore = factory.FakeIntelligence.StreamPromptCallCount;

        foreach (bool stream in new[] { false, true })
        {
            string payload = $$"""{"model":"mistral:latest","messages":[{"role":"user","content":"hello"}],"stream":{{stream.ToString().ToLowerInvariant()}},{{fields}}}""";

            using StringContent content = new(payload, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            JsonElement error = body.RootElement.GetProperty("error");

            Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());

            Assert.Equal("invalid_value", error.GetProperty("code").GetString());

            Assert.Equal(parameter, error.GetProperty("param").GetString());

            OpenAiChatRequest request = JsonSerializer.Deserialize(payload, ArcanumJsonContext.Default.OpenAiChatRequest)!;

            OpenAiV1Endpoints.ChatCompletionValidationFailure? batchFailure = OpenAiV1Endpoints.TryValidateChatCompletionRequest(
                request, new ArcanumSettings(), out _, forceDisableAllTools: true);

            Assert.NotNull(batchFailure);

            Assert.Equal(400, batchFailure.StatusCode);

            Assert.Equal(parameter, batchFailure.Param);
        }

        Assert.Equal(bufferedBefore, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(streamedBefore, factory.FakeIntelligence.StreamPromptCallCount);
    }
}
