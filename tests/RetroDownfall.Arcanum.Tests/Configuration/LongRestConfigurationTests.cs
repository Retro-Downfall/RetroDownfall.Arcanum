using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class LongRestConfigurationTests
{
    [Fact]
    public void LongRest_defaults_off_and_round_trips_without_enabling_rollups()
    {
        ArcanumSettings settings = new();

        using JsonDocument defaults = JsonDocument.Parse(JsonSerializer.Serialize(settings, ConfigurationJsonContext.Default.ArcanumSettings));

        Assert.True(defaults.RootElement.GetProperty("features").TryGetProperty("longRest", out JsonElement gate));

        Assert.False(gate.GetBoolean());

        ArcanumSettings enabled = JsonSerializer.Deserialize("""{"features":{"longRest":true}}""", ConfigurationJsonContext.Default.ArcanumSettings)!;

        using JsonDocument roundtrip = JsonDocument.Parse(JsonSerializer.Serialize(enabled, ConfigurationJsonContext.Default.ArcanumSettings));

        Assert.True(roundtrip.RootElement.GetProperty("features").GetProperty("longRest").GetBoolean());

        Assert.False(enabled.Features.CampaignRollups);

        Assert.False(enabled.ResolveIntelligence().EnableCampaignRollups);
    }

    [Theory]
    [InlineData("configured-provider", "configured-model", true)]
    [InlineData(null, "configured-model", false)]
    [InlineData("configured-provider", null, false)]
    [InlineData(" ", "configured-model", false)]
    [InlineData("configured-provider", "\t", false)]
    public void LongRest_derives_only_configured_embedding_availability(string? provider, string? model, bool available)
    {
        ArcanumSettings settings = JsonSerializer.Deserialize("""{"features":{"longRest":true}}""", ConfigurationJsonContext.Default.ArcanumSettings)!;

        settings.Integrations.Embeddings = new() { Provider = provider, Model = model, Dimensions = 17 };

        EmbeddingSettings runtime = settings.ResolveEmbeddings();

        Assert.Equal(available, runtime.Enabled);

        Assert.Equal(provider, runtime.Provider);

        Assert.Equal(model, runtime.Model);

        Assert.Equal(17, runtime.Dimensions);

        Assert.False(runtime.SagaEnabled);

        Assert.False(runtime.Saga.ExtractionEnabled);

        Assert.False(runtime.TapestryEnabled);

        Assert.False(settings.Features.Embeddings);
    }
}
