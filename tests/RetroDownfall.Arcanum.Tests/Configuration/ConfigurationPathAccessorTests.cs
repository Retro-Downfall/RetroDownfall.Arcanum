using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationPathAccessorTests
{

    [Fact]

    public void Set_resolves_generated_descriptor_path_and_parses_integer()
    {

        ArcanumSettings settings = new();

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            settings,
            "host.port",
            "6123");

        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal(6123, result.Settings!.Host.Port);

        Assert.Equal("6123", ConfigurationPathAccessor.GetDisplayValue(result.Settings, "host.port"));

    }

    [Fact]

    public void Set_supports_indexed_collection_paths_and_redacts_sensitive_values()
    {

        ArcanumSettings settings = new()
        {

            Providers =
            [
                new ProviderSettings
                {

                    Name = "OpenAI",

                    Endpoint = "https://old.example/v1",

                },
            ],

        };

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            settings,
            "providers.0.endpoint",
            "https://new.example/v1");

        Assert.True(result.IsSuccess, result.Error);

        Assert.Equal("https://new.example/v1", result.Settings!.Providers[0].Endpoint);

        Assert.True(ConfigurationPathAccessor.IsSensitive("providers.0.endpoint"));

        Assert.Equal("***", ConfigurationPathAccessor.GetDisplayValue(result.Settings, "providers.0.endpoint"));

    }

    [Theory]

    [InlineData("host.listenAny", "true")]

    [InlineData("edition", "Development")]

    [InlineData("security.allowedImageMimeTypes", "image/png,image/jpeg")]

    public void Set_parses_supported_typed_values(string key, string value)
    {

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            new ArcanumSettings(),
            key,
            value);

        Assert.True(result.IsSuccess, result.Error);

    }

    [Fact]

    public void Set_rejects_unknown_paths_without_mutating_snapshot()
    {

        ArcanumSettings settings = new();

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            settings,
            "cache.enabled",
            "true");

        Assert.False(result.IsSuccess);

        Assert.Same(settings, result.Settings);

        Assert.Contains("Unknown configuration key", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Set_rejects_invalid_typed_value()
    {

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            new ArcanumSettings(),
            "host.port",
            "not-a-number");

        Assert.False(result.IsSuccess);

        Assert.Contains("integer", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Set_rejects_invalid_json_array_without_throwing()
    {

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            new ArcanumSettings(),
            "security.allowedImageMimeTypes",
            "[");

        Assert.False(result.IsSuccess);

        Assert.Contains("valid JSON", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Set_resolves_model_descriptor_paths_under_an_indexed_provider()
    {

        ArcanumSettings settings = NewSettingsWithOneModel();

        ConfigurationPathUpdate vision = ConfigurationPathAccessor.Set(
            settings,
            "providers.0.models.0.supportsVision",
            "true");

        Assert.True(vision.IsSuccess, vision.Error);

        Assert.True(vision.Settings!.Providers[0].Models[0].SupportsVision);

        ConfigurationPathUpdate name = ConfigurationPathAccessor.Set(
            vision.Settings,
            "providers.0.models.0.name",
            "gpt-4o-mini");

        Assert.True(name.IsSuccess, name.Error);

        Assert.Equal("gpt-4o-mini", name.Settings!.Providers[0].Models[0].Name);

        ConfigurationPathUpdate dialect = ConfigurationPathAccessor.Set(
            name.Settings,
            "providers.0.models.0.reasoning.wireDialect",
            "openRouter");

        Assert.True(dialect.IsSuccess, dialect.Error);

        Assert.Equal(
            ReasoningWireDialect.OpenRouter,
            dialect.Settings!.Providers[0].Models[0].Reasoning!.WireDialect);

        ConfigurationPathUpdate budget = ConfigurationPathAccessor.Set(
            dialect.Settings,
            "providers.0.models.0.reasoning.maxBudgetTokens",
            "4096");

        Assert.True(budget.IsSuccess, budget.Error);

        Assert.Equal(4096, budget.Settings!.Providers[0].Models[0].Reasoning!.MaxBudgetTokens);

    }

    [Fact]

    public void Get_reads_model_descriptor_paths_under_an_indexed_provider()
    {

        ArcanumSettings settings = NewSettingsWithOneModel();

        Assert.True(ConfigurationPathAccessor.Exists(settings, "providers.0.models.0.supportsVision"));

        Assert.Equal(
            "gpt-4o",
            ConfigurationPathAccessor.GetDisplayValue(settings, "providers.0.models.0.name"));

        Assert.Equal(
            "false",
            ConfigurationPathAccessor.GetDisplayValue(settings, "providers.0.models.0.supportsVision"));

    }

    [Fact]

    public void Set_rejects_an_out_of_range_model_index()
    {

        ConfigurationPathUpdate result = ConfigurationPathAccessor.Set(
            NewSettingsWithOneModel(),
            "providers.0.models.3.supportsVision",
            "true");

        Assert.False(result.IsSuccess);

        Assert.Contains("collection index", result.Error, StringComparison.OrdinalIgnoreCase);

    }

    private static ArcanumSettings NewSettingsWithOneModel() =>
        new()
        {

            Providers =
            [
                new ProviderSettings
                {

                    Name = "OpenAI",

                    Endpoint = "https://api.example/v1",

                    Models = [new ModelEntry("gpt-4o")],

                },
            ],

        };

}
