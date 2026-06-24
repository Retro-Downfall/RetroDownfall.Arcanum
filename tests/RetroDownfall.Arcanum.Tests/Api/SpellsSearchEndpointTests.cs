using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SpellsSearchEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellsSearchEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetSpellsSearch_WithValidApiKey_ReturnsSpellSummaryEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateAllowedWorkspace();

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/spells/search?workspace={Uri.EscapeDataString(workspace)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellSummary[]>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task GetSpellsSearch_WithQuery_ReturnsOkEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateAllowedWorkspace();

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/spells/search?workspace={Uri.EscapeDataString(workspace)}&q=nonexistent-spell-name");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellSummary[]>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    private string CreateAllowedWorkspace()
    {

        string workspace = Path.Combine(_factory.TempHome, "spell-workspace");

        Directory.CreateDirectory(workspace);

        return workspace;

    }

}
