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

    /// <summary>
    /// <c>Spell.WriteFailed</c> is the repository saying the filesystem refused it, not the caller
    /// saying something wrong. The create route answered an unconditional 400 for every failure, so a
    /// client had no way to tell "fix your request" from "the server could not write". API §8.23 puts
    /// it with <c>Workspace.WriteFailed</c> at 500.
    /// </summary>
    [SkippableFact]
    public async Task CreateSpell_answers_500_when_the_workspace_filesystem_refuses_the_write()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(
            _factory.TempHome,
            $"spell-mutation-writefail-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        // A regular file where the repository must create the "spells" directory: the staging
        // Directory.CreateDirectory throws, which is exactly the arm that answers Spell.WriteFailed.
        await File.WriteAllTextAsync(Path.Combine(workspace, "spells"), "not a directory");

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = JsonSerializer.Serialize(
            new CreateSpellRequest(
                "blocked-spell",
                "a spell whose workspace cannot hold it",
                [],
                "system",
                null,
                null,
                null,
                [],
                []),
            ArcanumJsonContext.Default.CreateSpellRequest);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/spells?workspace={Uri.EscapeDataString(workspace)}",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        ApiResponse<bool>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(envelope);

        Assert.Equal(ErrorCodes.Spell.WriteFailed, envelope.Error?.Code);

    }

    /// <summary>
    /// <c>SpellExportDto.FullContent</c> and <c>.Scripts</c> are non-nullable positional parameters, so
    /// STJ happily binds them to null when the body omits them and the repository then dereferences
    /// null well past the point where a request could still be refused. Rejecting at the binding layer
    /// is what makes the declared shape true.
    /// </summary>
    [SkippableTheory]
    [InlineData("""{"payload":{},"workspace":"WORKSPACE"}""")]
    [InlineData("""{"payload":{"metadata":null,"scripts":[]},"workspace":"WORKSPACE"}""")]
    [InlineData("""{"payload":{"metadata":null,"fullContent":"---\nname: x\n---\nbody"},"workspace":"WORKSPACE"}""")]
    public async Task ImportSpell_refuses_a_payload_missing_a_required_member(string template)
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(
            _factory.TempHome,
            $"spell-import-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string payload = template.Replace(
            "WORKSPACE",
            JsonEncodedText.Encode(workspace).ToString(),
            StringComparison.Ordinal);

        HttpResponseMessage response = await client.PostAsync(
            "/api/spells/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

}
