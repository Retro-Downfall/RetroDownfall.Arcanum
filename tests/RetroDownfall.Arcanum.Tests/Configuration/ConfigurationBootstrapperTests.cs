using Microsoft.Extensions.Configuration;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]

public sealed class ConfigurationBootstrapperTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string? _originalTestHome;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalTestHome = global::System.Environment.GetEnvironmentVariable(
            "ARCANUM_TEST_HOME");

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _workspace.Root);

    }

    public async Task DisposeAsync()
    {

        global::System.Environment.SetEnvironmentVariable(
            "ARCANUM_TEST_HOME",
            _originalTestHome);

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

    [Theory]

    [InlineData(
        """{"Arcanum":{"security":{"ward":{"enabled":true}}}}""",
        "security.ward.enabled")]

    [InlineData(
        """{"Arcanum":{"security":{"ward":{"autoDenyInUnattendedMode":true}}}}""",
        "security.ward.autoDenyInUnattendedMode")]

    [InlineData(
        """{"Arcanum":{"security":{"ward":{"autoApprove":{"enabled":true}}}}}""",
        "security.ward.autoApprove")]

    [InlineData(
        """{"Arcanum":{"security":{"ward":{"autoApprove":{"tools":["write_file"]}}}}}""",
        "security.ward.autoApprove")]

    public void LoadArcanumSettingsFile_rejects_removed_Ward_keys_from_disk(
        string json,
        string removedPath)
    {

        string path = Path.Combine(_workspace.Root, "removed-ward-arcanum.json");

        File.WriteAllText(path, json);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.LoadArcanumSettingsFile(path));

        Assert.Contains(removedPath, exception.Message, StringComparison.Ordinal);

        Assert.Contains("remove", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void LoadArcanumSettingsFile_TreatsAnExplicitNullModelPricingMapAsEmpty()
    {
        string path = Path.Combine(_workspace.Root, "null-model-pricing-arcanum.json");
        File.WriteAllText(
            path,
            """{"Arcanum":{"cost":{"pricing":{"modelPricing":null}}}}""");

        ArcanumSettings settings =
            ConfigurationBootstrapper.LoadArcanumSettingsFile(path);

        Assert.Empty(settings.Cost.Pricing.ModelPricing);
        Assert.Same(
            settings.Cost.Pricing.DefaultPricing,
            settings.Cost.Pricing.ResolveForModel("mistral:latest"));
    }

    [Fact]
    public void LoadArcanumSettingsFile_AcceptsTheDocumentedTapestryRetrievalModeName()
    {
        string path = Path.Combine(_workspace.Root, "tapestry-retrieval-mode-arcanum.json");
        File.WriteAllText(
            path,
            """
            {
              "Arcanum": {
                "integrations": {
                  "embeddings": {
                    "tapestry": {
                      "retrievalMode": "TreeTraversal"
                    }
                  }
                }
              }
            }
            """);

        ArcanumSettings settings =
            ConfigurationBootstrapper.LoadArcanumSettingsFile(path);

        Assert.Equal(
            TapestryRetrievalMode.TreeTraversal,
            settings.Integrations.Embeddings.Tapestry.RetrievalMode);
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

    public void LoadArcanumSettingsFile_applies_documented_general_environment_overrides()
    {

        const string variable = "ARCANUM_Arcanum__Host__Port";

        string? original = global::System.Environment.GetEnvironmentVariable(variable);

        string path = Path.Combine(_workspace.Root, "environment-layered-arcanum.json");

        File.WriteAllText(
            path,
            """{"Arcanum":{"host":{"port":5001}}}""");

        try
        {

            global::System.Environment.SetEnvironmentVariable(variable, "6124");

            ArcanumSettings settings =
                ConfigurationBootstrapper.LoadArcanumSettingsFile(path);

            Assert.Equal(6124, settings.Host.Port);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(variable, original);

        }

    }

    [Theory]

    [InlineData(
        "ARCANUM_Arcanum__Security__Ward__Enabled",
        "security.ward.enabled")]

    [InlineData(
        "ARCANUM_Arcanum__Security__Ward__AutoDenyInUnattendedMode",
        "security.ward.autoDenyInUnattendedMode")]

    [InlineData(
        "ARCANUM_Arcanum__Security__Ward__AutoApprove__Enabled",
        "security.ward.autoApprove")]

    [InlineData(
        "ARCANUM_Arcanum__Security__Ward__AutoApprove__Tools__0",
        "security.ward.autoApprove")]

    public void LoadArcanumSettingsFile_rejects_removed_Ward_environment_overrides(
        string variable,
        string removedPath)
    {

        string? original = global::System.Environment.GetEnvironmentVariable(variable);

        string path = Path.Combine(_workspace.Root, "environment-removed-ward-arcanum.json");

        File.WriteAllText(path, """{"Arcanum":{"host":{"port":5001}}}""");

        try
        {

            global::System.Environment.SetEnvironmentVariable(variable, "true");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => ConfigurationBootstrapper.LoadArcanumSettingsFile(path));

            Assert.Contains(removedPath, exception.Message, StringComparison.Ordinal);

            Assert.Contains("remove", exception.Message, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(variable, original);

        }

    }

    [Fact]

    public void AddArcanumConfiguration_projects_general_overrides_for_listener_configuration()
    {

        const string variable = "ARCANUM_Arcanum__Host__Port";

        string? original = global::System.Environment.GetEnvironmentVariable(variable);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(
            ArcanumPaths.ConfigurationFile,
            """{"Arcanum":{"host":{"port":5001}}}""");

        try
        {

            global::System.Environment.SetEnvironmentVariable(variable, "6124");

            ConfigurationManager configuration = new();

            configuration.AddArcanumConfiguration();

            Assert.Equal("6124", configuration["Arcanum:Host:Port"]);

            Assert.Equal(6124, ServeCommand.ReadConfiguredHostPort(configuration));

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(variable, original);

        }

    }

    [Theory]
    [InlineData("""["https://ui.internal"]""")]
    [InlineData("https://ui.internal")]
    public void AddArcanumConfiguration_projects_array_overrides_as_indexed_children(string rawValue)
    {

        const string variable = "ARCANUM_Arcanum__Host__CorsAllowedOrigins";

        string? original = global::System.Environment.GetEnvironmentVariable(variable);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(
            ArcanumPaths.ConfigurationFile,
            """{"Arcanum":{"host":{"corsAllowedOrigins":["http://localhost:5001","http://localhost:3000","http://127.0.0.1:3000"]}}}""");

        try
        {

            global::System.Environment.SetEnvironmentVariable(variable, rawValue);

            ConfigurationManager configuration = new();

            configuration.AddArcanumConfiguration();

            IConfigurationSection section = configuration.GetSection("Arcanum:Host:CorsAllowedOrigins");

            Assert.True(section.Exists());

            string[] origins = section
                .GetChildren()
                .Select(static child => child.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToArray();

            Assert.Equal(["https://ui.internal"], origins);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(variable, original);

        }

    }

    [Fact]
    public void AddArcanumConfiguration_projects_array_overrides_when_file_omits_the_key()
    {

        const string variable = "ARCANUM_Arcanum__Host__CorsAllowedOrigins";

        string? original = global::System.Environment.GetEnvironmentVariable(variable);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(
            ArcanumPaths.ConfigurationFile,
            """{"Arcanum":{"host":{"port":5001}}}""");

        try
        {

            global::System.Environment.SetEnvironmentVariable(
                variable,
                """["https://ui.internal","https://ops.internal"]""");

            ConfigurationManager configuration = new();

            configuration.AddArcanumConfiguration();

            IConfigurationSection section = configuration.GetSection("Arcanum:Host:CorsAllowedOrigins");

            Assert.True(section.Exists());

            string[] origins = section
                .GetChildren()
                .Select(static child => child.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToArray();

            Assert.Equal(["https://ui.internal", "https://ops.internal"], origins);

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable(variable, original);

        }

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

    [Fact]
    public void ValidateArcanumConfigurationFile_oversized_file_fails_before_parsing()
    {

        string path = Path.Combine(_workspace.Root, "oversized-arcanum.json");

        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {

            stream.SetLength(ConfigurationBootstrapper.MaxConfigurationBytes + 1L);

        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ConfigurationBootstrapper.ValidateArcanumConfigurationFile(path));

        Assert.Contains("configuration exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);

    }

}
