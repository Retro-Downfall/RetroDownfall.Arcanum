using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /api/spells/import</c> answers a failure with the same status the create, update and delete
/// routes give it, through the shared <c>SpellApiResults.MapFailure</c>.
/// </summary>
[Collection("ApiHost")]
public sealed class SpellImportEndpointTests(ArcanumWebApplicationFactory factory)
{
    [SkippableFact]
    public async Task Import_with_an_unwritable_workspace_answers_500_WriteFailed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(factory.TempHome, $"spell-import-writefail-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        // A regular file where the repository must create the "spells" directory: the staging
        // Directory.CreateDirectory throws, which is the arm that answers Spell.WriteFailed.
        await File.WriteAllTextAsync(Path.Combine(workspace, "spells"), "not a directory");

        HttpResponseMessage response = await PostImportAsync(
            workspace,
            """{"metadata":null,"fullContent":"---\nname: blocked-import\n---\nbody","scripts":[]}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.Equal(ErrorCodes.Spell.WriteFailed, await ReadErrorCodeAsync(response));
    }

    [SkippableFact]
    public async Task Import_with_invalid_base64_script_answers_400()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(factory.TempHome, $"spell-import-base64-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        HttpResponseMessage response = await PostImportAsync(
            workspace,
            """{"metadata":null,"fullContent":"---\nname: bad-script\n---\nbody","scripts":[{"fileName":"run.sh","base64Content":"@@ not base64 @@"}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, await ReadErrorCodeAsync(response));

        // Nothing was staged or published for the refused bundle.
        Assert.False(Directory.Exists(Path.Combine(workspace, "spells", "bad-script")));
    }

    [SkippableFact]
    public async Task Import_with_a_script_path_that_escapes_the_scripts_directory_answers_400()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(factory.TempHome, $"spell-import-escape-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        string content = Convert.ToBase64String(Encoding.UTF8.GetBytes("echo hi"));

        HttpResponseMessage response = await PostImportAsync(
            workspace,
            $$"""{"metadata":null,"fullContent":"---\nname: escaping-script\n---\nbody","scripts":[{"fileName":"../run.sh","base64Content":"{{content}}"}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("Spell.InvalidScriptPath", await ReadErrorCodeAsync(response));
    }

    private async Task<HttpResponseMessage> PostImportAsync(string workspace, string payloadJson)
    {
        HttpClient client = factory.CreateAuthenticatedClient();

        string body = "{\"payload\":" + payloadJson + ",\"workspace\":" + "\"" + JsonEncodedText.Encode(workspace) + "\"" + "}";

        return await client.PostAsync(
            "/api/spells/import",
            new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        ApiResponse<SpellSummary>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSpellSummary);

        Assert.NotNull(envelope);

        Assert.False(envelope.IsSuccess);

        return envelope.Error?.Code;
    }
}
