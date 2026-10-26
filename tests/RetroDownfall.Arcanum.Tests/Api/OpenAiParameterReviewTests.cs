using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class OpenAiParameterReviewTests
{
    [SkippableTheory]
    [InlineData("\"logprobs\":true", "logprobs")]
    [InlineData("\"top_logprobs\":0", "top_logprobs")]
    [InlineData("\"logprobs\":false,\"top_logprobs\":3", "top_logprobs")]
    public async Task Unsupported_logprob_request_is_refused_before_buffered_or_streaming_dispatch(string fields, string parameter)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        foreach (bool stream in new[] { false, true })
        {
            string payload = $$"""{"model":"mistral:latest","messages":[{"role":"user","content":"private-prompt-canary"}],"stream":{{stream.ToString().ToLowerInvariant()}},{{fields}}}""";

            using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            string json = await response.Content.ReadAsStringAsync();

            OpenAiErrorResponse error = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse)!;

            Assert.Equal("unsupported_parameter", error.Error.Code);

            Assert.Equal(parameter, error.Error.Param);

            Assert.Contains(parameter, error.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("private-prompt-canary", json, StringComparison.Ordinal);
        }

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(0, factory.FakeIntelligence.StreamPromptCallCount);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData(",\"logprobs\":false")]
    public async Task Omitted_or_disabled_logprobs_still_dispatches(string fields)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        foreach (bool stream in new[] { false, true })
        {
            string payload = $$"""{"model":"mistral:latest","messages":[{"role":"user","content":"hello"}],"stream":{{stream.ToString().ToLowerInvariant()}}{{fields}}}""";

            using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            _ = await response.Content.ReadAsStringAsync();
        }

        Assert.Equal(1, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(1, factory.FakeIntelligence.StreamPromptCallCount);
    }
}
