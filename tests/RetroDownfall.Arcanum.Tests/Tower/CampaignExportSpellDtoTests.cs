using System.Text.Json;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Tower;

public sealed class CampaignExportSpellDtoTests
{

    [Fact]
    public void ResolvedSpellJson_prefers_spellJson_over_legacy_skillJson()
    {

        CampaignExportSpellDto dto = new(
            "s",
            SpellJson: "{\"name\":\"canonical\"}",
            FullContent: "body",
            Scripts: [],
            SkillJson: "{\"name\":\"legacy\"}");

        Assert.Equal("{\"name\":\"canonical\"}", dto.ResolvedSpellJson);

    }

    [Fact]
    public void ResolvedSpellJson_falls_back_to_legacy_skillJson()
    {

        CampaignExportSpellDto dto = new(
            "s",
            SpellJson: null,
            FullContent: "body",
            Scripts: [],
            SkillJson: "{\"name\":\"legacy\"}");

        Assert.Equal("{\"name\":\"legacy\"}", dto.ResolvedSpellJson);

    }

    [Fact]
    public void Serialize_writes_spellJson_and_omits_null_skillJson()
    {

        CampaignExportSpellDto dto = new(
            "s",
            SpellJson: "{\"name\":\"n\"}",
            FullContent: "body",
            Scripts: []);

        string json = JsonSerializer.Serialize(dto, ArcanumCoreJsonContext.Default.CampaignExportSpellDto);

        Assert.Contains("\"spellJson\"", json, StringComparison.Ordinal);

        Assert.DoesNotContain("\"skillJson\"", json, StringComparison.Ordinal);

    }

    [Fact]
    public void Deserialize_accepts_legacy_skillJson()
    {

        const string json = """{"name":"s","skillJson":"{\"name\":\"legacy\"}","fullContent":"body","scripts":[]}""";

        CampaignExportSpellDto? dto = JsonSerializer.Deserialize(json, ArcanumCoreJsonContext.Default.CampaignExportSpellDto);

        Assert.NotNull(dto);

        Assert.Equal("{\"name\":\"legacy\"}", dto!.ResolvedSpellJson);

    }

}
