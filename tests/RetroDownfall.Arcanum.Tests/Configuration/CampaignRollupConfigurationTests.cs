using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]
public sealed class CampaignRollupConfigurationTests : IDisposable
{
    private readonly ArcanumTestHomeScope _home = new("arcanum-campaign-rollup-config");

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"campaignRollups\":true}", true)]
    [InlineData("{\"campaignRollups\":false}", false)]
    public void Canonical_configuration_projects_the_explicit_campaign_rollup_gate(string features, bool expected)
    {
        string path = Path.Combine(_home.Root, "campaign-rollups.json");

        File.WriteAllText(path, "{\"Arcanum\":{\"features\":" + features + "}}");

        ArcanumSettings settings = ConfigurationBootstrapper.LoadArcanumSettingsFile(path);

        using JsonDocument roundtrip = JsonDocument.Parse(JsonSerializer.Serialize(
            settings,
            ConfigurationJsonContext.Default.ArcanumSettings));

        Assert.True(roundtrip.RootElement.GetProperty("features").TryGetProperty("campaignRollups", out JsonElement gate));

        Assert.Equal(expected, gate.GetBoolean());

        IntelligenceSettings runtime = settings.ResolveIntelligence();

        Assert.Equal(expected, runtime.EnableCampaignRollups);
    }

    public void Dispose() => _home.Dispose();
}
