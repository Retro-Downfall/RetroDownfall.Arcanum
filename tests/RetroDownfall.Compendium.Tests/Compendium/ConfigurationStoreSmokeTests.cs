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

    private readonly ArcanumTestHomeScope _home;

    private readonly string _tempRoot;

    public ConfigurationStoreSmokeTests()
    {

        _home = new ArcanumTestHomeScope("compendium-smoke");

        _tempRoot = _home.Root;

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

    [Fact]

    public async Task WriteAsync_does_not_mutate_until_the_shared_transaction_runs_operation()
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
                "host": {
                  "port": 5001
                }
              }
            }
            """);

        int transactionCalls = 0;

        using ArcanumConfigurationStore store = new(
            enableWatcher: false,
            transactionRunner: (_, _) =>
            {

                transactionCalls++;

                return Task.FromResult(
                    new ConfigurationWriteResult(false, [], "Transaction held."));

            });

        ConfigurationWriteResult result = await store.WriteAsync(
            new ArcanumSettings
            {

                Host = new HostSettings { Port = 6124 },

            },
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Equal("Transaction held.", result.ErrorMessage);

        Assert.Equal(1, transactionCalls);

        string unchanged = await File.ReadAllTextAsync(configPath);

        Assert.Contains("5001", unchanged, StringComparison.Ordinal);

        Assert.DoesNotContain("6124", unchanged, StringComparison.Ordinal);

        Assert.Empty(Directory.EnumerateFiles(
            ArcanumPaths.GrimoireDirectory,
            ".arcanum.*.tmp"));

    }

    [Fact]

    public async Task WriteAsync_rejects_a_competing_change_after_the_last_read()
    {

        string configPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            configPath,
            """{"Arcanum":{"host":{"port":5001}}}""");

        using ArcanumConfigurationStore store = new(
            enableWatcher: false,
            transactionRunner: async (operation, _) =>
            {

                await File.WriteAllTextAsync(
                    configPath,
                    """{"Arcanum":{"host":{"port":6124}}}""");

                return await operation();

            });

        _ = await store.ReadAsync(CancellationToken.None);

        ConfigurationWriteResult result = await store.WriteAsync(
            new ArcanumSettings
            {

                Host = new HostSettings { Port = 7333 },

            },
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        Assert.Contains(
            "changed on disk",
            result.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);

        string retained = await File.ReadAllTextAsync(configPath);

        Assert.Contains("6124", retained, StringComparison.Ordinal);

        Assert.DoesNotContain("7333", retained, StringComparison.Ordinal);

        Assert.Empty(Directory.EnumerateFiles(
            ArcanumPaths.GrimoireDirectory,
            ".arcanum.*.tmp"));

    }

    /// <summary>
    /// A save has to leave <c>arcanum.json</c> owner-only whatever it started as. Hardening only the
    /// staging file is not enough: on Windows <c>ReplaceFile</c> keeps the replaced file's DACL, so a
    /// destination that arrived with a loose ACL (restored from a backup, dropped in by an installer,
    /// created before the directory was hardened) would stay readable by other principals — while the
    /// same edit through <c>arcanum config set</c> tightens it, because <c>ConfigurationWriter</c>
    /// re-applies owner-only permissions to the destination after the replace.
    /// </summary>
    [Fact]

    public async Task WriteAsync_hardens_the_destination_that_arrived_with_loose_permissions()
    {

        string configPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.json");

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            configPath,
            """{"Arcanum":{"host":{"port":5001}}}""");

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(
                configPath,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead
                | UnixFileMode.OtherRead);

        }

        using ArcanumConfigurationStore store = new(enableWatcher: false);

        _ = await store.ReadAsync(CancellationToken.None);

        ConfigurationWriteResult result = await store.WriteAsync(
            new ArcanumSettings
            {

                Host = new HostSettings { Port = 6124 },

            },
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        if (OperatingSystem.IsWindows())
        {

            Assert.True(
                new FileInfo(configPath)
                    .GetAccessControl()
                    .AreAccessRulesProtected);

            return;

        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(configPath));

    }

    [Fact]

    public async Task Mutation_reload_acknowledges_atomic_replacement_without_hiding_later_external_edit()
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
                "host": {
                  "port": 5001
                }
              }
            }
            """);

        using ArcanumConfigurationStore store = new(enableWatcher: false);

        int externalChanges = 0;

        store.ExternalChange += (_, _) => externalChanges++;

        _ = await store.ReadAsync(CancellationToken.None);

        string replacementPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            ".preset-replacement.tmp");

        await File.WriteAllTextAsync(
            replacementPath,
            """
            {
              "Arcanum": {
                "host": {
                  "port": 6124
                }
              }
            }
            """);

        File.Move(replacementPath, configPath, overwrite: true);

        ArcanumSettings reloadedAfterMutation = await store.ReadAsync(
            CancellationToken.None);

        Assert.Equal(6124, reloadedAfterMutation.Host.Port);

        await store.ProcessObservedChangeAsync(CancellationToken.None);

        Assert.Equal(0, externalChanges);

        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "Arcanum": {
                "host": {
                  "port": 7333
                }
              }
            }
            """);

        await store.ProcessObservedChangeAsync(CancellationToken.None);

        Assert.Equal(1, externalChanges);

    }

    public void Dispose() => _home.Dispose();

}
