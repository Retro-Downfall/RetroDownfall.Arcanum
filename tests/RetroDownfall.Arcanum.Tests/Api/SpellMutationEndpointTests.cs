using System.Net;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SpellMutationEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellMutationEndpointTests(
        ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    private async Task<string> CreateWorkspaceWithSpellAsync(string suffix)
    {

        string workspace = Path.Combine(
            _factory.TempHome,
            $"spell-mutation-{suffix}-{Guid.NewGuid():N}");

        string spellDirectory = Path.Combine(workspace, "spells", "resident-spell");

        Directory.CreateDirectory(spellDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(spellDirectory, "SPELL.md"),
            "---\nname: resident-spell\ndescription: an existing workspace spell\n---\nbody");

        return workspace;

    }

    [SkippableFact]
    public async Task DeleteSpell_returns_404_when_the_workspace_spell_does_not_exist()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = await CreateWorkspaceWithSpellAsync("delete");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.DeleteAsync(
            $"/api/spells/nonexistent-spell?workspace={Uri.EscapeDataString(workspace)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<bool>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(envelope);

        Assert.False(envelope.IsSuccess);

        Assert.Equal(ErrorCodes.Spell.NotFound, envelope.Error?.Code);

    }

    [SkippableFact]
    public async Task UpdateSpell_returns_404_when_the_workspace_spell_does_not_exist()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = await CreateWorkspaceWithSpellAsync("update");

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = JsonSerializer.Serialize(
            new UpdateSpellRequest(
                "a new description",
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            ArcanumJsonContext.Default.UpdateSpellRequest);

        HttpResponseMessage response = await client.PutAsync(
            $"/api/spells/nonexistent-spell?workspace={Uri.EscapeDataString(workspace)}",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<bool>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(envelope);

        Assert.False(envelope.IsSuccess);

        Assert.Equal(ErrorCodes.Spell.NotFound, envelope.Error?.Code);

    }

    [SkippableFact]
    public async Task UpdateSpell_still_returns_400_when_the_frontmatter_is_invalid()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = await CreateWorkspaceWithSpellAsync("frontmatter");

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = JsonSerializer.Serialize(
            new UpdateSpellRequest(
                "a description\nspanning lines",
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            ArcanumJsonContext.Default.UpdateSpellRequest);

        HttpResponseMessage response = await client.PutAsync(
            $"/api/spells/resident-spell?workspace={Uri.EscapeDataString(workspace)}",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<bool>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(envelope);

        Assert.Equal("Spell.InvalidFrontmatter", envelope.Error?.Code);

    }

}
