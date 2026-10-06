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

        Assert.False(string.IsNullOrWhiteSpace(body.Data.EmbeddingsVectorMode));

        Assert.Equal(0, body.Data.EmbeddingsManagedSearchRowBudget);

        Assert.DoesNotContain("llamaCppEnabled", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("LlamaCppEnabled", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every flag <c>/api/meta</c> reports is read from the configuration the host resolved, so it moves with
    /// it, and none is a constant that no setting can change.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Meta_fields_reflect_configuration(bool archiveSearch)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { ArchiveSearch = archiveSearch },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/meta");

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InstanceMetadataDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseInstanceMetadataDto);

        Assert.NotNull(body?.Data);

        Assert.Equal(archiveSearch, body.Data.ArchiveSearchEnabled);

        // Flags no setting can move are not configuration and are not reported as if they were: the retired
        // Lore system's flag was always false, and context compression and token tracking are code-owned on.
        Assert.DoesNotContain("loreSystemEnabled", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("contextCompressionEnabled", json, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("tokenTrackingEnabled", json, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task GetMeta_ReportsConclaveA2AStateAndSurfaces()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/meta");

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<InstanceMetadataDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseInstanceMetadataDto);

        Assert.NotNull(body?.Data);

        // A2A is off by default, so meta must say so explicitly rather than omit the subsystem.
        Assert.Equal("disabled", body.Data.ConclaveA2AState);

        Assert.False(body.Data.ConclaveEnabled);

        Assert.False(body.Data.A2AServerEnabled);

        Assert.False(body.Data.A2AClientEnabled);

        Assert.Null(body.Data.A2AServerPath);
    }
}
