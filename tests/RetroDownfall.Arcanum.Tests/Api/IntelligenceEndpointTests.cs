using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class IntelligenceEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public IntelligenceEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostPing_Buffered_ReturnsFakeIntelligenceText()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextToolCalls = null;

        _factory.FakeIntelligence.NextFinishReason = null;

        _factory.FakeIntelligence.NextText = "buffered-pong";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "ping");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/ping",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PromptResponseDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal("buffered-pong", body.Data.Text);

        Assert.Equal("ping", _factory.FakeIntelligence.LastPrompt);

    }

    [SkippableFact]
    public async Task PostPing_MissingPrompt_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = "{}";

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/ping",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PromptResponseDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Validation.InvalidPrompt", body.Error?.Code);

    }

}
