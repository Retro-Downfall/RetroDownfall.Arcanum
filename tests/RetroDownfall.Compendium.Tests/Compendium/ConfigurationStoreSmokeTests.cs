using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Services;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class ConfigurationStoreSmokeTests : IDisposable
{

    private readonly string _originalHome;

    private readonly string _originalUserProfile;

    private readonly string _tempRoot;

    public ConfigurationStoreSmokeTests()
    {

        _originalHome = global::System.Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        _originalUserProfile = global::System.Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        _tempRoot = Path.Combine(Path.GetTempPath(), $"compendium-smoke-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        global::System.Environment.SetEnvironmentVariable("HOME", _tempRoot);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _tempRoot);

    }

    [Fact]

    public async Task RoundTrip_preserves_provider_credential_reference_and_host_port()
    {
        ArcanumSettings seed = new()

        {

            Host = new HostSettings { Port = 5001 },

            Providers =
            [

                new ProviderSettings

                {

                    Name = "openai",

                    Type = AiProviderKind.OpenAICompatible,

                    Endpoint = "https://api.openai.com/v1",

                    CredentialEnvironmentVariable = "OPENAI_API_KEY",

                    Models = ["gpt-4o"],

                    ContextWindowLimit = 8192,

                },

            ],
            Integrations = new IntegrationSettings
            {
                CommLink = new CommLinkIntegrationSettings
                {
                    WebhookUrlEnvironmentVariable =
                        "ARCANUM_COMMLINK_WEBHOOK_URL",
                },
            },

        };

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(
                new ArcanumConfigurationFile { Arcanum = seed },
                ConfigurationJsonContext.Default.ArcanumConfigurationFile));

        using ArcanumConfigurationStore store = new();

        ArcanumSettings read = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(5001, read.Host.Port);

        Assert.Single(read.Providers);

        Assert.Equal(
            "OPENAI_API_KEY",
            read.Providers[0].CredentialEnvironmentVariable);
        Assert.Equal(
            "ARCANUM_COMMLINK_WEBHOOK_URL",
            read.Integrations.CommLink.WebhookUrlEnvironmentVariable);

        ArcanumSettings edited = read with

        {

            Host = read.Host with { Port = 9001 },

        };

        ConfigurationWriteResult writeResult = await store.WriteAsync(edited, CancellationToken.None);

        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);

        ArcanumSettings reread = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(9001, reread.Host.Port);

        Assert.Single(reread.Providers);

        Assert.Equal(
            "OPENAI_API_KEY",
            reread.Providers[0].CredentialEnvironmentVariable);

        string savedJson = await File.ReadAllTextAsync(configPath);

        Assert.Contains("OPENAI_API_KEY", savedJson, StringComparison.Ordinal);
        Assert.Contains(
            "ARCANUM_COMMLINK_WEBHOOK_URL",
            savedJson,
            StringComparison.Ordinal);

        Assert.DoesNotContain("\"apiKey\"", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"webhookUrl\"", savedJson, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ReadAsync_rejects_obsolete_provider_secret_values()
    {
        string configPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");
        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "Arcanum": {
                "providers": [
                  {
                    "name": "old",
                    "apiKey": "must-not-be-accepted",
                    "models": ["model"]
                  }
                ]
              }
            }
            """);
        using ArcanumConfigurationStore store = new();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ReadAsync(CancellationToken.None));

        Assert.Contains(
            "CredentialEnvironmentVariable",
            error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "must-not-be-accepted",
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"providers":[]}""", "providers")]
    [InlineData("""{"Arcanum":{"host":{"retiredOption":true}}}""", "host.retiredOption")]
    public async Task ReadAsync_rejects_wrong_root_and_unknown_nested_paths(
        string json,
        string expectedPointer)
    {
        string configPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");
        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);
        await File.WriteAllTextAsync(configPath, json);
        using ArcanumConfigurationStore store = new();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ReadAsync(CancellationToken.None));

        Assert.Contains(
            expectedPointer,
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_rejects_configuration_larger_than_supported_limit()
    {

        string configPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            configPath,
            "{\"Arcanum\":{\"padding\":\""
            + new string('x', ArcanumConfigurationStore.MaxConfigurationBytes)
            + "\"}}");

        using ArcanumConfigurationStore store = new();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ReadAsync(CancellationToken.None));

        Assert.Contains("exceeds", error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task WriteAsync_removes_staging_file_when_destination_replace_fails()
    {

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string destination = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");

        _ = Directory.CreateDirectory(destination);

        using ArcanumConfigurationStore store = new();

        ConfigurationWriteResult result = await store.WriteAsync(
            new ArcanumSettings(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Empty(Directory.EnumerateFiles(
            ArcanumPaths.GrimoireDirectory,
            ".arcanum.*.tmp"));

    }

    public void Dispose()
    {

        try

        {

            Directory.Delete(_tempRoot, recursive: true);

        }
        catch

        {

            // Best-effort cleanup.

        }

        global::System.Environment.SetEnvironmentVariable("HOME", _originalHome);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);

    }

}
