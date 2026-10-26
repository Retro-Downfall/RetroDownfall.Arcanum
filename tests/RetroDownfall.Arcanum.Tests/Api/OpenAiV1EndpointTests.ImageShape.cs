using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class OpenAiV1EndpointTests
{
    [SkippableTheory]
    [InlineData("""{"type":"image_url"}""", false)]
    [InlineData("""{"type":"image_url"}""", true)]
    [InlineData("""{"type":"image_url","image_url":null}""", false)]
    [InlineData("""{"type":"image_url","image_url":null}""", true)]
    [InlineData("""{"type":"image_url","image_url":{}}""", false)]
    [InlineData("""{"type":"image_url","image_url":{}}""", true)]
    [InlineData("""{"type":"image_url","image_url":{"url":null}}""", false)]
    [InlineData("""{"type":"image_url","image_url":{"url":null}}""", true)]
    [InlineData("""{"type":"image_url","image_url":{"url":""}}""", false)]
    [InlineData("""{"type":"image_url","image_url":{"url":""}}""", true)]
    [InlineData("""{"type":"image_url","image_url":{"url":" \t\r\n"}}""", false)]
    [InlineData("""{"type":"image_url","image_url":{"url":" \t\r\n"}}""", true)]
    [InlineData("""{"type":"image_url","image_url":{"url":"private-image-canary.png"}}""", false)]
    [InlineData("""{"type":"image_url","image_url":{"url":"private-image-canary.png"}}""", true)]
    [InlineData("""{"type":"image_url","image_url":{"url":"data:image/png,private-image-canary"}}""", false)]
    [InlineData("""{"type":"image_url","image_url":{"url":"data:image/png,private-image-canary"}}""", true)]
    public async Task PostChatCompletions_ImageShape_RejectsOriginalWireFieldBeforeDispatch(string imagePart, bool stream)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateImageShapeFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        string streamJson = stream ? "true" : "false";

        string payload = $$$"""
            {"model":"vision-model","stream":{{{streamJson}}},"messages":[
              {"role":"system","content":"private-input-canary"},
              {"role":"user","content":[{"type":"text","text":"private-input-canary"},null,{{{imagePart}}}]}
            ]}
            """;

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse error = Assert.IsType<OpenAiErrorResponse>(JsonSerializer.Deserialize(
            json, ArcanumJsonContext.Default.OpenAiErrorResponse));

        Assert.Equal("invalid_request_error", error.Error.Type);

        Assert.Equal("invalid_image_data", error.Error.Code);

        Assert.Contains("messages[1].content[2].image_url.url", error.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-input-canary", json, StringComparison.Ordinal);

        Assert.DoesNotContain("private-image-canary", json, StringComparison.Ordinal);

        Assert.Equal(0, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(0, factory.FakeIntelligence.StreamPromptCallCount);

        Assert.Null(factory.FakeIntelligence.LastRequest);
    }

    [SkippableTheory]
    [InlineData("https://images.example.test/image.png", false)]
    [InlineData("https://images.example.test/image.png", true)]
    [InlineData("data:image/png;base64,AQID", false)]
    [InlineData("data:image/png;base64,AQID", true)]
    public async Task PostChatCompletions_ImageShape_PreservesValidImageDispatch(string imageUrl, bool stream)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateImageShapeFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        string streamJson = stream ? "true" : "false";

        string urlJson = JsonSerializer.Serialize(imageUrl, ArcanumJsonContext.Default.String);

        string payload = $$$"""
            {"model":"vision-model","stream":{{{streamJson}}},"messages":[
              {"role":"user","content":[{"type":"text","text":"look"},{"type":"image_url","image_url":{"url":{{{urlJson}}},"detail":"low"}}]}
            ]}
            """;

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions", new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(stream ? 0 : 1, factory.FakeIntelligence.ExecutePromptCallCount);

        Assert.Equal(stream ? 1 : 0, factory.FakeIntelligence.StreamPromptCallCount);

        var message = Assert.Single(factory.FakeIntelligence.LastRequest!.StatelessMessages!);

        Assert.Equal(imageUrl, message.ContentParts![1].ImageUrl);

        Assert.Equal("low", message.ContentParts[1].ImageDetail);
    }

    private static ArcanumWebApplicationFactory CreateImageShapeFactory() => new()
    {
        SettingsOverride = settings => settings with
        {
            Features = settings.Features with { Scrying = true },
            DefaultModel = "vision-model",
            Providers =
            [
                new ProviderSettings
                {
                    Name = "vision-provider",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1",
                    Models = [new ModelEntry("vision-model", SupportsVision: true)],
                },
            ],
        },
    };
}
