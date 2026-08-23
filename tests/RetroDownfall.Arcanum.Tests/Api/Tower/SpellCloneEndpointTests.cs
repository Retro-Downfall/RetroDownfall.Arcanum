using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class SpellCloneEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellCloneEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    private string CreateWorkspace(string suffix)
    {

        string workspace = Path.Combine(_factory.TempHome, $"clone-ws-{suffix}");

        Directory.CreateDirectory(workspace);

        return workspace;

    }

    private static async Task WriteSpellAsync(string workspace, string name, string body)
    {

        string spellDir = Path.Combine(workspace, "spells", name);

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(Path.Combine(spellDir, "SPELL.md"), $"---\nname: {name}\ndescription: A test spell\n---\n\n{body}");

    }

    private async Task<HttpResponseMessage> CloneAsync(HttpClient client, string name, CloneSpellRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CloneSpellRequest);

        return await client.PostAsync(
            $"/api/spells/{name}/clone",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    [SkippableFact]
    public async Task Clone_creates_new_spell()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("basic");

        await WriteSpellAsync(workspace, "source-spell", "Original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CloneAsync(client, "source-spell", new CloneSpellRequest("cloned-spell", workspace));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellSummary>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellSummary);

        Assert.NotNull(body?.Data);

        Assert.Equal("cloned-spell", body.Data!.Name);

        Assert.True(File.Exists(Path.Combine(workspace, "spells", "cloned-spell", "SPELL.md")));

    }

    [SkippableFact]
    public async Task Clone_400_on_name_collision()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("collision");

        await WriteSpellAsync(workspace, "source-spell", "Original body.");

        await WriteSpellAsync(workspace, "existing-spell", "Already here.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CloneAsync(client, "source-spell", new CloneSpellRequest("existing-spell", workspace));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task Clone_400_on_invalid_name()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("invalidname");

        await WriteSpellAsync(workspace, "source-spell", "Original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CloneAsync(client, "source-spell", new CloneSpellRequest("not a valid name!", workspace));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task Clone_404_when_source_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("nosource");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CloneAsync(client, "does-not-exist", new CloneSpellRequest("new-name", workspace));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task Clone_allows_builtin_to_workspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("builtin");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage listResponse = await client.GetAsync($"/api/spells?workspace={Uri.EscapeDataString(workspace)}");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        string listJson = await listResponse.Content.ReadAsStringAsync();

        ApiResponse<SpellSummary[]>? listBody = JsonSerializer.Deserialize(listJson, ArcanumJsonContext.Default.ApiResponseSpellSummaryArray);

        SpellSummary? builtin = listBody?.Data?.FirstOrDefault(s => s.Source == SpellSource.Builtin);

        Skip.If(builtin is null, "No built-in spells are registered in this environment.");

        HttpResponseMessage response = await CloneAsync(client, builtin!.Name, new CloneSpellRequest($"{builtin.Name}-cloned", workspace));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    }

}
