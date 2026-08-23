using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class SpellCastEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellCastEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    private string CreateWorkspace(string suffix)
    {

        string workspace = Path.Combine(_factory.TempHome, $"cast-ws-{suffix}");

        Directory.CreateDirectory(workspace);

        return workspace;

    }

    private static async Task WriteSpellAsync(string workspace, string name, string body, string[]? declaredTools = null)
    {

        string spellDir = Path.Combine(workspace, "spells", name);

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(Path.Combine(spellDir, "SPELL.md"), $"---\nname: {name}\ndescription: A test spell\n---\n\n{body}");

        if (declaredTools is { Length: > 0 })
        {

            string skillJson = $$"""
                {"name":"{{name}}","version":"1.0.0","description":null,"tags":[],"inputSchema":null,"outputSchema":null,"declaredTools":[{{string.Join(",", declaredTools.Select(t => $"\"{t}\""))}}],"dependencies":[],"model":null,"provider":null,"defaultParameters":null,"lastModified":null}
                """;

            await File.WriteAllTextAsync(Path.Combine(spellDir, "SKILL.json"), skillJson);

        }

    }

    private async Task<HttpResponseMessage> CastAsync(HttpClient client, string name, SpellCastRequest request)
    {

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.SpellCastRequest);

        return await client.PostAsync(
            $"/api/spells/{name}/cast",
            new StringContent(payload, Encoding.UTF8, "application/json"));

    }

    [SkippableFact]
    public async Task Cast_returns_system_prompt_without_inference()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("basic");

        await WriteSpellAsync(workspace, "greet", "Say hello to the operator.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CastAsync(client, "greet", new SpellCastRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellCastResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellCastResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal("greet", body.Data.SpellName);

        Assert.Contains("Say hello to the operator.", body.Data.SystemPrompt, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task Cast_404_when_spell_not_found()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("missing");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CastAsync(client, "does-not-exist", new SpellCastRequest(workspace));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task Cast_400_when_no_workspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CastAsync(client, "greet", new SpellCastRequest(Workspace: "/definitely/not/allowed/path"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]
    public async Task Cast_includes_resonant_dependencies()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("deps");

        await WriteSpellAsync(workspace, "helper", "Helper spell body.");

        string primaryDir = Path.Combine(workspace, "spells", "primary");

        Directory.CreateDirectory(primaryDir);

        await File.WriteAllTextAsync(Path.Combine(primaryDir, "SPELL.md"), "---\nname: primary\ndescription: Primary spell\n---\n\nPrimary body.");

        await File.WriteAllTextAsync(
            Path.Combine(primaryDir, "SKILL.json"),
            """{"name":"primary","version":"1.0.0","description":null,"tags":[],"inputSchema":null,"outputSchema":null,"declaredTools":[],"dependencies":["helper"],"model":null,"provider":null,"defaultParameters":null,"lastModified":null}""");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CastAsync(client, "primary", new SpellCastRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellCastResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellCastResult);

        Assert.NotNull(body?.Data);

        Assert.Contains("helper", body.Data!.ResonantDependencies);

        Assert.Contains("Helper spell body.", body.Data.SystemPrompt, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task Cast_includes_available_tools_with_attunement()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = CreateWorkspace("tools");

        await WriteSpellAsync(workspace, "attuned", "Attuned spell body.", declaredTools: ["get_local_system_time"]);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await CastAsync(client, "attuned", new SpellCastRequest(workspace));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SpellCastResult>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellCastResult);

        Assert.NotNull(body?.Data);

        Assert.True(body.Data!.HasDeclaredToolsFilter);

    }

}
