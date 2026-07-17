using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class MetaEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public MetaEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetMeta_WithValidApiKey_ReturnsInstanceMetadataEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/meta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InstanceMetadataDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseInstanceMetadataDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.False(string.IsNullOrWhiteSpace(body.Data.Version));

        Assert.False(string.IsNullOrWhiteSpace(body.Data.GrimoireDirectory));

        Assert.False(body.Data.HttpsEnabled);

        Assert.Null(body.Data.HttpsUrl);

        Assert.Equal($"http://localhost:{body.Data.Port}", body.Data.HttpUrl);

        Assert.Equal(5443, body.Data.HttpsPort);

        Assert.DoesNotContain("llamaCppEnabled", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("LlamaCppEnabled", json, StringComparison.Ordinal);

    }

}
