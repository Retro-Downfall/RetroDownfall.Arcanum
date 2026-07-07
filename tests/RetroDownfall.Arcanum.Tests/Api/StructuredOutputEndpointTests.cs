using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class StructuredOutputEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public StructuredOutputEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [Fact]
    public async Task PostChatCompletions_Buffered_Warning_SetsHeaderAndSystemFingerprint()
    {

        _factory.FakeIntelligence.NextText = """{"name": "Alice"}""";

        _factory.FakeIntelligence.NextWarnings = ["validation failed after 2 retries"];

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(response.Headers.Contains("X-Arcanum-Structured-Output-Warning"));

        IEnumerable<string> values = response.Headers.GetValues("X-Arcanum-Structured-Output-Warning");

        Assert.Contains(values, v => v.Contains("validation failed after 2 retries", StringComparison.OrdinalIgnoreCase));

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("arcanum:structured-output-warning", body, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task PostChatCompletions_Buffered_NoWarning_NoHeader()
    {

        _factory.FakeIntelligence.NextText = """{"name": "Alice"}""";

        _factory.FakeIntelligence.NextWarnings = [];

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "hello" }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.False(response.Headers.Contains("X-Arcanum-Structured-Output-Warning"));

    }

    [Fact]
    public async Task PostChatCompletions_Streaming_Warning_SetsSystemFingerprint()
    {

        _factory.FakeIntelligence.NextText = """{"name": "Alice"}""";

        _factory.FakeIntelligence.NextWarnings = ["streamed response failed JSON schema validation"];

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = """
            {
              "model": "mistral:latest",
              "messages": [
                { "role": "user", "content": "hello" }
              ],
              "stream": true
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("text/event-stream; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("arcanum:structured-output-warning", body, StringComparison.OrdinalIgnoreCase);

    }

}
