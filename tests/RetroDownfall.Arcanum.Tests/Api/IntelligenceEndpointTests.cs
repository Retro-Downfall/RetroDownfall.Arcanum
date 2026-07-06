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
    public async Task PostPing_Buffered_PassesAuditContextWithRequestTypeAndClientIp()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "pong";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "ping");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/ping",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        InferenceAuditContext? auditContext = _factory.FakeIntelligence.LastAuditContext;

        Assert.NotNull(auditContext);

        Assert.Equal("ping", auditContext.RequestType);

    }

    [SkippableFact]
    public async Task PostPingStream_PassesAuditContextWithRequestType()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _factory.FakeIntelligence.NextFailure = null;

        _factory.FakeIntelligence.NextText = "streamed-pong";

        HttpClient client = _factory.CreateAuthenticatedClient();

        PingRequest request = new(Prompt: "ping");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.PingRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/ping-stream",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        _ = await response.Content.ReadAsStringAsync();

        InferenceAuditContext? auditContext = _factory.FakeIntelligence.LastAuditContext;

        Assert.NotNull(auditContext);

        Assert.Equal("ping-stream", auditContext.RequestType);

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

    [SkippableFact]
    public async Task PostMana_WithPrompt_ReturnsManaCount()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        ManaCountRequest request = new(Prompt: "hello world");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ManaCountRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/mana",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ManaCountResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseManaCountResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.True(body.Data.ManaCount > 0);

        Assert.Equal("o200k_base", body.Data.Encoding);

        Assert.Null(body.Data.PerMessage);

        Assert.Null(body.Data.ToolManaEstimate);

    }

    [SkippableFact]
    public async Task PostMana_WithMessages_ReturnsPerMessageBreakdown()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        ManaCountRequest request = new(Messages:
        [
            new CoreChatMessage("system", "You are a helpful assistant."),
            new CoreChatMessage("user", "What is the capital of France?"),
        ]);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ManaCountRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/mana",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ManaCountResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseManaCountResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.NotNull(body.Data.PerMessage);

        Assert.Equal(2, body.Data.PerMessage!.Count);

        Assert.Equal(body.Data.PerMessage.Sum(), body.Data.ManaCount);

    }

    [SkippableFact]
    public async Task PostMana_WithToolsTrue_ReturnsToolManaEstimate()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        ManaCountRequest request = new(Prompt: "hello", Tools: true);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ManaCountRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/mana",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ManaCountResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseManaCountResult);

        Assert.NotNull(body);

        Assert.NotNull(body.Data);

        Assert.NotNull(body.Data.ToolManaEstimate);

        Assert.True(body.Data.ToolManaEstimate!.Value > 0);

    }

    [SkippableFact]
    public async Task PostMana_MissingMessagesAndPrompt_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = "{}";

        HttpResponseMessage response = await client.PostAsync(
            "/api/intelligence/mana",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ManaCountResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseManaCountResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Validation.InvalidBody", body.Error?.Code);

    }

}
