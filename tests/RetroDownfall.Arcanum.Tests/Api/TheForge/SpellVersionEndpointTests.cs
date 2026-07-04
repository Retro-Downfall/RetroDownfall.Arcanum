using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class SpellVersionEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellVersionEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    private string CreateWorkspace(string suffix)
    {

        string workspace = Path.Combine(_factory.TempHome, $"version-ws-{suffix}");

        Directory.CreateDirectory(workspace);

        return workspace;

    }

    private static string SpellDir(string workspace, string name) => Path.Combine(workspace, "spells", name);

    private static async Task WriteSpellAsync(string workspace, string name, string body)
    {

        string spellDir = SpellDir(workspace, name);

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(Path.Combine(spellDir, "SPELL.md"), $"---\nname: {name}\ndescription: A test spell\n---\n\n{body}");

    }

    private async Task<HttpResponseMessage> CreateVersionAsync(HttpClient client, string name, CreateSpellVersionRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateSpellVersionRequest);

        return await client.PostAsync(
            $"/api/spells/{name}/versions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private async Task<HttpResponseMessage> UpdateVersionAsync(HttpClient client, string name, string version, UpdateSpellVersionRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.UpdateSpellVersionRequest);

        return await client.PutAsync(
            $"/api/spells/{name}/versions/{Uri.EscapeDataString(version)}",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    private async Task<HttpResponseMessage> ActivateVersionAsync(HttpClient client, string name, string version, ActivateSpellVersionRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.ActivateSpellVersionRequest);

        return await client.PostAsync(
            $"/api/spells/{name}/versions/{Uri.EscapeDataString(version)}/activate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    [SkippableFact]
    public async Task CreateVersion_creates_versioned_file()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("create");

        await WriteSpellAsync(workspace, "versioned", "Original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("2.0", "New draft body.", workspace));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellVersionDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellVersionDto);

        Assert.NotNull(body?.Data);

        Assert.Equal("2.0", body.Data!.Version);

        Assert.False(body.Data.IsActive);

        Assert.True(File.Exists(Path.Combine(SpellDir(workspace, "versioned"), "SPELL.v2.0.md")));

    }

    [SkippableFact]
    public async Task CreateVersion_400_on_duplicate_version()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("dup");

        await WriteSpellAsync(workspace, "versioned", "Original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage first = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("1.1", "First draft.", workspace));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        HttpResponseMessage second = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("1.1", "Second draft.", workspace));

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

    }

    [SkippableFact]
    public async Task CreateVersion_400_on_invalid_label()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("invalidlabel");

        await WriteSpellAsync(workspace, "versioned", "Original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("not valid!", "Body.", workspace));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task UpdateVersion_overwrites_body()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("update");

        await WriteSpellAsync(workspace, "versioned", "Original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage created = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("1.5", "First draft.", workspace));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        HttpResponseMessage updated = await UpdateVersionAsync(client, "versioned", "1.5", new UpdateSpellVersionRequest("Updated draft.", workspace));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        string content = await File.ReadAllTextAsync(Path.Combine(SpellDir(workspace, "versioned"), "SPELL.v1.5.md"));

        Assert.Contains("Updated draft.", content, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task UpdateVersion_404_when_version_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("missingupdate");

        await WriteSpellAsync(workspace, "versioned", "Original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await UpdateVersionAsync(client, "versioned", "9.9", new UpdateSpellVersionRequest("Body.", workspace));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task ActivateVersion_swaps_spell_md()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("activate");

        await WriteSpellAsync(workspace, "versioned", "Original active body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage created = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("2.0", "Version two body.", workspace));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        HttpResponseMessage activated = await ActivateVersionAsync(client, "versioned", "2.0", new ActivateSpellVersionRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        string activeContent = await File.ReadAllTextAsync(Path.Combine(SpellDir(workspace, "versioned"), "SPELL.md"));

        Assert.Contains("Version two body.", activeContent, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task ActivateVersion_preserves_previous_as_v0_when_no_activeVersion()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("v0fallback");

        await WriteSpellAsync(workspace, "versioned", "Pristine original body.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage created = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("3.0", "Version three body.", workspace));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        HttpResponseMessage activated = await ActivateVersionAsync(client, "versioned", "3.0", new ActivateSpellVersionRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        string json = await activated.Content.ReadAsStringAsync();

        ApiResponse<SpellVersionDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellVersionDto);

        Assert.Equal("0", body?.Data?.PreviousVersion);

        string preservedContent = await File.ReadAllTextAsync(Path.Combine(SpellDir(workspace, "versioned"), "SPELL.v0.md"));

        Assert.Contains("Pristine original body.", preservedContent, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task ActivateVersion_generates_timestamp_label_when_v0_exists()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("timestampfallback");

        await WriteSpellAsync(workspace, "versioned", "Body A.");

        await File.WriteAllTextAsync(Path.Combine(SpellDir(workspace, "versioned"), "SPELL.v0.md"), "Pre-existing v0.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage created = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("4.0", "Body B.", workspace));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        HttpResponseMessage activated = await ActivateVersionAsync(client, "versioned", "4.0", new ActivateSpellVersionRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        string json = await activated.Content.ReadAsStringAsync();

        ApiResponse<SpellVersionDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellVersionDto);

        Assert.NotNull(body?.Data?.PreviousVersion);

        Assert.NotEqual("0", body!.Data!.PreviousVersion);

        Assert.Equal(14, body.Data.PreviousVersion!.Length);

    }

    [SkippableFact]
    public async Task ActivateVersion_preserves_previous_with_activeVersion_label()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("activelabel");

        await WriteSpellAsync(workspace, "versioned", "Body A.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage createdOne = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("1.0", "Body one.", workspace));

        Assert.Equal(HttpStatusCode.Created, createdOne.StatusCode);

        HttpResponseMessage firstActivate = await ActivateVersionAsync(client, "versioned", "1.0", new ActivateSpellVersionRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, firstActivate.StatusCode);

        HttpResponseMessage createdTwo = await CreateVersionAsync(client, "versioned", new CreateSpellVersionRequest("2.0", "Body two.", workspace));

        Assert.Equal(HttpStatusCode.Created, createdTwo.StatusCode);

        HttpResponseMessage secondActivate = await ActivateVersionAsync(client, "versioned", "2.0", new ActivateSpellVersionRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, secondActivate.StatusCode);

        string json = await secondActivate.Content.ReadAsStringAsync();

        ApiResponse<SpellVersionDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellVersionDto);

        Assert.Equal("1.0", body?.Data?.PreviousVersion);

    }

}
