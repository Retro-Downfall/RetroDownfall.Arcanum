using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Operational_notice_header_is_separate_from_schema_warning_and_fingerprint(bool schemaWarning, bool notice)
    {
        IReadOnlyList<InferenceNotice>? oldNotices = _factory.FakeIntelligence.NextNotices;

        List<string>? oldWarnings = _factory.FakeIntelligence.NextWarnings;

        try
        {
            _factory.FakeIntelligence.NextWarnings = schemaWarning ? ["schema-only warning"] : [];

            _factory.FakeIntelligence.NextNotices = notice
                ? [new(InferenceNoticeCode.ToolCompatibilityRestart), new(InferenceNoticeCode.ToolCompatibilityRestart)]
                : [new((InferenceNoticeCode)int.MaxValue)];

            using HttpClient client = _factory.CreateAuthenticatedClient();

            using HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
                new StringContent("{\"model\":\"mistral:latest\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(schemaWarning, body.Contains("arcanum:structured-output-warning", StringComparison.Ordinal));

            Assert.Equal(schemaWarning, response.Headers.Contains("X-Arcanum-Structured-Output-Warning"));

            Assert.Equal(notice, response.Headers.Contains("X-Arcanum-Notice"));

            Assert.DoesNotContain("notices", body, StringComparison.OrdinalIgnoreCase);

            if (notice)
            {
                string header = Assert.Single(response.Headers.GetValues("X-Arcanum-Notice"));

                Assert.Equal(new InferenceNotice(InferenceNoticeCode.ToolCompatibilityRestart).Message, header);

                Assert.InRange(header.Length, 1, 256);

                Assert.All(header, static character => Assert.InRange(character, ' ', '~'));
            }
        }
        finally
        {
            _factory.FakeIntelligence.NextNotices = oldNotices;

            _factory.FakeIntelligence.NextWarnings = oldWarnings;
        }
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

    // Validation warnings quote model-supplied JSON property names verbatim, so a non-ASCII or
    // control character in the model's output would reach Kestrel's response-header validation and
    // throw — turning an already-billed 200 completion into a 500. The header must be reduced to
    // printable US-ASCII instead.
    [Fact]
    public async Task PostChatCompletions_Buffered_NonAsciiWarning_StillReturnsTheCompletion()
    {
        _factory.FakeIntelligence.NextText = """{"name": "Alice"}""";

        _factory.FakeIntelligence.NextWarnings =
            ["validation failed after correction stopped: $.: additional property 'résumé\r\n' is not allowed."];

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

        string headerValue = Assert.Single(
            response.Headers.GetValues("X-Arcanum-Structured-Output-Warning"));

        Assert.All(headerValue, character => Assert.InRange(character, ' ', '~'));

        Assert.Contains("additional property", headerValue, StringComparison.Ordinal);

        Assert.DoesNotContain('é', headerValue);
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
