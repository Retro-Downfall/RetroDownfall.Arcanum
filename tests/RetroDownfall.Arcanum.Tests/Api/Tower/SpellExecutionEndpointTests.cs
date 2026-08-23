using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class SpellExecutionEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellExecutionEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task ExecuteSpell_ValidationInvalidPrompt_Returns400()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string spellName = "test-validation";

        string spellDir = Path.Combine(_factory.TempHome, spellName);

        Directory.CreateDirectory(spellDir);

        await File.WriteAllTextAsync(Path.Combine(spellDir, "SPELL.md"), $"# {spellName}\n\n---\n\nTest spell.");

        _factory.FakeIntelligence.NextFailure = new Error("Validation.InvalidPrompt", "Prompt is required.");

        HttpClient client = _factory.CreateAuthenticatedClient();

        SpellExecuteRequest body = new(Prompt: "hello");

        string payload = JsonSerializer.Serialize(body, ArcanumJsonContext.Default.SpellExecuteRequest);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/spells/{spellName}/execute?workspace={Uri.EscapeDataString(_factory.TempHome)}",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        _factory.FakeIntelligence.NextFailure = null;

    }

}
