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

}
