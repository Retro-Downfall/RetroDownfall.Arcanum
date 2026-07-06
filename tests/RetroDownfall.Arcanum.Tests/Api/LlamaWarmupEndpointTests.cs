using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /api/llama/servers/{cacheKey}/warmup</c>. The shared "ApiHost" test factory never starts
/// a real <c>llama-server</c> process, so only the "no server running" 400 path is exercised here —
/// the running-server success path requires an actual llama-server binary and is verified manually /
/// in a real deployment (see DESIGN.md §8.20). Model-name resolution logic is covered independently
/// by <c>LlamaWarmupModelResolutionTests</c>.
/// </summary>
[Collection("ApiHost")]
public sealed class LlamaWarmupEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public LlamaWarmupEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostWarmup_NoRunningServer_Returns400ServerNotRunning()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/llama/servers/no-such-cache-key/warmup",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WarmupResultDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWarmupResultDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Llama.ServerNotRunning", body.Error?.Code);

    }

    [SkippableFact]
    public async Task PostWarmup_NoRunningServer_CustomPromptBody_Returns400ServerNotRunning()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        WarmupRequestDto request = new() { Prompt = "ping", MaxTokens = 4 };

        HttpResponseMessage response = await client.PostAsync(
            "/api/llama/servers/no-such-cache-key/warmup",
            new StringContent(
                JsonSerializer.Serialize(request, ArcanumJsonContext.Default.WarmupRequestDto),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<WarmupResultDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseWarmupResultDto);

        Assert.NotNull(body);

        Assert.Equal("Llama.ServerNotRunning", body.Error?.Code);

    }

    [SkippableFact]
    public async Task PostWarmup_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/llama/servers/no-such-cache-key/warmup",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

}
