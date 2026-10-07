using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SpellExportEndpointTests(ArcanumWebApplicationFactory factory)
{
    /// <summary>
    /// The export names the scripts it leaves out on the wire, under a camelCase <c>omittedScripts</c>, so a client
    /// that never reads the server log can still tell a partial bundle from a complete one.
    /// </summary>
    [SkippableFact]
    public async Task ExportSpell_names_the_scripts_the_bundle_leaves_out()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(factory.TempHome, $"spell-export-{Guid.NewGuid():N}");

        string scriptsDirectory = Path.Combine(workspace, "spells", "bundle-spell", "scripts");

        Directory.CreateDirectory(scriptsDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, "spells", "bundle-spell", "SPELL.md"),
            "---\nname: bundle-spell\ndescription: a spell with scripts\n---\nbody");

        await File.WriteAllBytesAsync(Path.Combine(scriptsDirectory, "ok.sh"), "echo ok"u8.ToArray());

        long perFileCap = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes();

        await File.WriteAllBytesAsync(Path.Combine(scriptsDirectory, "too-big.sh"), new byte[checked((int)perFileCap + 1)]);

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            $"/api/spells/bundle-spell/export?workspace={Uri.EscapeDataString(workspace)}",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"omittedScripts\":[\"too-big.sh\"]", json, StringComparison.Ordinal);

        Assert.Contains("\"omittedScriptCount\":1", json, StringComparison.Ordinal);

        ApiResponse<SpellExportDto>? envelope = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseSpellExportDto);

        Assert.NotNull(envelope);

        SpellExportDto exported = envelope.Data!;

        Assert.Equal("ok.sh", Assert.Single(exported.Scripts).FileName);

        Assert.Equal(["too-big.sh"], exported.OmittedScripts);
    }

    /// <summary>
    /// <c>omittedScripts</c> is something an export reports, not something an import needs: a bundle that still
    /// carries it (the file a client saved from an export) is imported as before, and a bundle from before the
    /// field existed imports too.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportSpell_accepts_a_bundle_with_or_without_omittedScripts(bool carriesOmittedScripts)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(factory.TempHome, $"spell-import-omitted-{Guid.NewGuid():N}");

        Directory.CreateDirectory(workspace);

        // The name comes from the bundle's own frontmatter when there is no metadata.
        string omittedMember = carriesOmittedScripts ? ",\"omittedScripts\":[\"never-carried.sh\"]" : string.Empty;

        string payload =
            "{\"payload\":{\"metadata\":null,\"fullContent\":\"---\\nname: omitted-ok\\n---\\nbody\",\"scripts\":[]"
            + omittedMember
            + "},\"workspace\":\""
            + JsonEncodedText.Encode(workspace)
            + "\"}";

        HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/spells/import",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(Directory.Exists(Path.Combine(workspace, "spells", "omitted-ok")));
    }
}
