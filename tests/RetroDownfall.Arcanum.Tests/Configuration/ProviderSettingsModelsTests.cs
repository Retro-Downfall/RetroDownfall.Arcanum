using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Configuration;

/// <summary>
/// <see cref="ProviderSettings.Models"/> is declared as an exact <see cref="ModelEntry"/> array so the
/// resolver and the validator index framework array storage, never an interface indexer. These tests pin
/// that the array leaves the wire shape (a JSON array, bare-string entries included) and the configuration
/// binding unchanged.
/// </summary>
public sealed class ProviderSettingsModelsTests
{
    private const string ProviderJson = """
        {
          "name": "openai",
          "type": "OpenAICompatible",
          "endpoint": "https://api.openai.com/v1",
          "models": [
            { "name": "gpt-4o", "supportsVision": true },
            "gpt-4o-mini"
          ],
          "contextWindowLimit": 128000
        }
        """;

    [Fact]
    public void Models_is_declared_as_an_exact_array_that_defaults_to_empty()
    {
        System.Reflection.PropertyInfo property = typeof(ProviderSettings).GetProperty(nameof(ProviderSettings.Models))!;

        Assert.Equal(typeof(ModelEntry[]), property.PropertyType);

        Assert.Empty(new ProviderSettings().Models);
    }

    [Fact]
    public void ConfigurationJsonContext_round_trips_models_as_a_json_array()
    {
        ProviderSettings provider = JsonSerializer.Deserialize(ProviderJson, ConfigurationJsonContext.Default.ProviderSettings)!;

        AssertMixedModels(provider);

        string written = JsonSerializer.Serialize(provider, ConfigurationJsonContext.Default.ProviderSettings);

        AssertWrittenAsArrayOfObjects(written);

        AssertMixedModels(JsonSerializer.Deserialize(written, ConfigurationJsonContext.Default.ProviderSettings)!);
    }

    [Fact]
    public void ArcanumJsonContext_round_trips_models_as_a_json_array()
    {
        ProviderSettings provider = JsonSerializer.Deserialize(ProviderJson, ArcanumJsonContext.Default.ProviderSettings)!;

        AssertMixedModels(provider);

        string written = JsonSerializer.Serialize(provider, ArcanumJsonContext.Default.ProviderSettings);

        AssertWrittenAsArrayOfObjects(written);

        AssertMixedModels(JsonSerializer.Deserialize(written, ArcanumJsonContext.Default.ProviderSettings)!);
    }

    [Fact]
    public void An_absent_models_property_reads_as_an_empty_array_and_an_empty_array_is_written()
    {
        ProviderSettings provider = JsonSerializer.Deserialize(
            """{"name":"claude","type":"ClaudeCodeCli"}""",
            ConfigurationJsonContext.Default.ProviderSettings)!;

        Assert.Empty(provider.Models);

        using JsonDocument written = JsonDocument.Parse(
            JsonSerializer.Serialize(provider, ConfigurationJsonContext.Default.ProviderSettings));

        JsonElement models = written.RootElement.GetProperty("models");

        Assert.Equal(JsonValueKind.Array, models.ValueKind);

        Assert.Equal(0, models.GetArrayLength());
    }

    [Fact]
    public void An_explicit_null_models_property_still_resolves_as_no_models()
    {
        ProviderSettings provider = JsonSerializer.Deserialize(
            """{"name":"claude","type":"ClaudeCodeCli","models":null}""",
            ConfigurationJsonContext.Default.ProviderSettings)!;

        Assert.Empty(ProviderResolver.EnumerateAdvertisedModels(provider));

        Assert.False(ProviderResolver.TryResolveModelEntry(provider, "anything", out ModelEntry? entry));

        Assert.Null(entry);
    }

    [Fact]
    public void Configure_binds_indexed_models_into_the_array_without_duplicating_the_default()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Providers:0:Name"] = "openai",
                ["Arcanum:Providers:0:Models:0:Name"] = "gpt-4o",
                ["Arcanum:Providers:0:Models:0:SupportsVision"] = "true",
                ["Arcanum:Providers:0:Models:1:Name"] = "gpt-4o-mini",
            })
            .Build();

        ServiceCollection services = new();

        services.Configure<ArcanumSettings>(configuration.GetSection("Arcanum"));

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        ProviderSettings provider = Assert.Single(
            serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Providers);

        Assert.Equal(2, provider.Models.Length);

        Assert.Equal("gpt-4o", provider.Models[0].Name);

        Assert.True(provider.Models[0].SupportsVision);

        Assert.Equal("gpt-4o-mini", provider.Models[1].Name);

        Assert.False(provider.Models[1].SupportsVision);
    }

    private static void AssertMixedModels(ProviderSettings provider)
    {
        Assert.Equal(2, provider.Models.Length);

        Assert.Equal("gpt-4o", provider.Models[0].Name);

        Assert.True(provider.Models[0].SupportsVision);

        Assert.Equal("gpt-4o-mini", provider.Models[1].Name);

        Assert.False(provider.Models[1].SupportsVision);
    }

    private static void AssertWrittenAsArrayOfObjects(string written)
    {
        using JsonDocument document = JsonDocument.Parse(written);

        JsonElement models = document.RootElement.GetProperty("models");

        Assert.Equal(JsonValueKind.Array, models.ValueKind);

        Assert.Equal(2, models.GetArrayLength());

        Assert.Equal("gpt-4o", models[0].GetProperty("name").GetString());

        Assert.True(models[0].GetProperty("supportsVision").GetBoolean());
    }
}
