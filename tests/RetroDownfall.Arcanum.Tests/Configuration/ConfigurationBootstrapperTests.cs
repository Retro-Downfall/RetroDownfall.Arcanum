using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class ConfigurationBootstrapperTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public void ValidateArcanumConfigurationFile_missing_file_does_not_throw()
    {

        string path = Path.Combine(_workspace.Root, "missing-arcanum.json");

        ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path);

    }

    [Fact]
    public void ValidateArcanumConfigurationFile_valid_json_does_not_throw()
    {

        string path = Path.Combine(_workspace.Root, "valid-arcanum.json");

        File.WriteAllText(path, """{"Arcanum":{"providers":[]}}""");

        ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path);

    }

    [Theory]
    [InlineData("""{"providers":[]}""")]
    [InlineData("""{"arcanum":{"providers":[]}}""")]
    [InlineData("""{"Arcanum":{"providers":[]},"UnexpectedRoot":{}}""")]
    [InlineData("""{"WrongRoot":{"providers":[]}}""")]
    public void ValidateArcanumConfigurationFile_wrong_root_throws(string json)
    {
        string path = Path.Combine(_workspace.Root, "wrong-root-arcanum.json");
        File.WriteAllText(path, json);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path));

        Assert.Contains("arcanum.json is invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateArcanumConfigurationFile_unknown_nested_paths_are_grouped()
    {
        string path = Path.Combine(_workspace.Root, "unknown-arcanum.json");
        File.WriteAllText(
            path,
            """
            {
              "Arcanum": {
                "host": {
                  "https": {
                    "unknownTlsOption": true
                  }
                },
                "daemon": {
                  "jobs": [
                    {
                      "name": "daily",
                      "unknownSchedule": "midnight"
                    }
                  ]
                }
              }
            }
            """);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path));

        Assert.Contains("host.https.unknownTlsOption", ex.Message, StringComparison.Ordinal);
        Assert.Contains("daemon.jobs[0].unknownSchedule", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadArcanumSettingsFile_UsesSourceGeneratedModelAndPricingShapes()
    {
        string path = Path.Combine(_workspace.Root, "source-generated-arcanum.json");
        File.WriteAllText(
            path,
            """
            {
              "Arcanum": {
                "providers": [
                  {
                    "name": "local",
                    "type": "OpenAICompatible",
                    "endpoint": "http://localhost:11434/v1",
                    "models": [ "mistral:latest" ],
                    "contextWindowLimit": 32768
                  }
                ],
                "cost": {
                  "pricing": {
                    "modelPricing": {
                      "mistral:latest": {
                        "inputPer1M": 1.25,
                        "outputPer1M": 2.5,
                        "cachedPer1M": 0.25
                      }
                    }
                  }
                }
              }
            }
            """);

        ArcanumSettings settings =
            ConfigurationBootstrapper.LoadArcanumSettingsFile(path);

        ModelEntry model = Assert.Single(Assert.Single(settings.Providers).Models);
        Assert.Equal("mistral:latest", model.Name);
        ModelPricingEntry pricing =
            settings.Cost.Pricing.ResolveForModel("mistral:latest");
        Assert.Equal(1.25m, pricing.InputPer1M);
        Assert.Equal(2.5m, pricing.OutputPer1M);
        Assert.Equal(0.25m, pricing.CachedPer1M);
    }

    [Fact]
    public void LoadArcanumSettingsFile_UsesTheValidatorsCaseInsensitivePropertyContract()
    {
        string path = Path.Combine(_workspace.Root, "pascal-case-arcanum.json");
        File.WriteAllText(
            path,
            """{"Arcanum":{"Host":{"Port":6123}}}""");

        ArcanumSettings settings =
            ConfigurationBootstrapper.LoadArcanumSettingsFile(path);

        Assert.Equal(6123, settings.Host.Port);
    }

    [Fact]
    public void ValidateArcanumConfigurationFile_invalid_json_throws()
    {

        string path = Path.Combine(_workspace.Root, "invalid-arcanum.json");

        File.WriteAllText(path, "{not-json");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path));

        Assert.Contains("arcanum.json is invalid", ex.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void ValidateArcanumConfigurationFile_null_root_throws()
    {

        string path = Path.Combine(_workspace.Root, "null-root-arcanum.json");

        File.WriteAllText(path, "null");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path));

        Assert.Contains("arcanum.json is invalid", ex.Message, StringComparison.Ordinal);

    }

}
