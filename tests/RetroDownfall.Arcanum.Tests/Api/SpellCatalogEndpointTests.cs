using System.Net;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Intelligence.Spells;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SpellCatalogEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SpellCatalogEndpointTests(
        ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetSpells_paged_returns_metadata_page_and_opaque_continuation()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(
            _factory.TempHome,
            $"spell-catalog-{Guid.NewGuid():N}");

        for (int index = 0; index < 55; index++)
        {

            string spellDirectory = Path.Combine(
                workspace,
                "spells",
                $"spell-{index:D3}");

            Directory.CreateDirectory(spellDirectory);

            await File.WriteAllTextAsync(
                Path.Combine(spellDirectory, "SPELL.md"),
                $"---\nname: spell-{index:D3}\ndescription: description {index:D3}\n---\nbody");

        }

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/spells?paged=true&source=Workspace&workspace={Uri.EscapeDataString(workspace)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SpellCatalogPage>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSpellCatalogPage);

        Assert.NotNull(envelope);

        Assert.True(envelope.IsSuccess);

        SpellCatalogPage page = Assert.IsType<SpellCatalogPage>(envelope.Data);

        Assert.Equal(50, page.Items.Length);

        Assert.True(page.HasMore);

        Assert.False(string.IsNullOrWhiteSpace(page.NextCursor));

        Assert.DoesNotContain(
            "spell-049",
            page.NextCursor,
            StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task GetSpells_without_paged_preserves_the_legacy_array_envelope()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        string workspace = Path.Combine(
            _factory.TempHome,
            $"legacy-spell-catalog-{Guid.NewGuid():N}");

        string spellDirectory = Path.Combine(
            workspace,
            "spells",
            "legacy-spell");

        Directory.CreateDirectory(spellDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(spellDirectory, "SPELL.md"),
            "---\nname: legacy-spell\ndescription: legacy array\n---\nbody");

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/spells?workspace={Uri.EscapeDataString(workspace)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SpellSummary[]>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray);

        Assert.NotNull(envelope);

        Assert.True(envelope.IsSuccess);

        Assert.Contains(
            envelope.Data ?? [],
            static spell => spell.Name == "legacy-spell");

    }

}
