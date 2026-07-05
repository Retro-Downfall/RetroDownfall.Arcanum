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

    public async Task RoundTrip_preserves_provider_api_key_and_host_port()
    {

        ArcanumDataProtectionSecretProtector protector = new();

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

                    ApiKey = "sk-test",

                    Models = ["gpt-4o"],

                    ContextWindowLimit = 8192,

                },

            ],

        };

        ArcanumSettings encryptedSeed = protector.EncryptProviderKeys(seed);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(
                new ArcanumConfigurationFile { Arcanum = encryptedSeed },
                ConfigurationJsonContext.Default.ArcanumConfigurationFile));

        using ArcanumConfigurationStore store = new(protector);

        ArcanumSettings read = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(5001, read.Host.Port);

        Assert.Single(read.Providers);

        Assert.Equal("sk-test", read.Providers[0].ApiKey);

        ArcanumSettings edited = read with

        {

            Host = read.Host with { Port = 9001 },

        };

        ConfigurationWriteResult writeResult = await store.WriteAsync(edited, CancellationToken.None);

        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);

        ArcanumSettings reread = await store.ReadAsync(CancellationToken.None);

        Assert.Equal(9001, reread.Host.Port);

        Assert.Single(reread.Providers);

        Assert.Equal("sk-test", reread.Providers[0].ApiKey);

        string savedJson = await File.ReadAllTextAsync(configPath);

        Assert.Contains("dp:v1:", savedJson, StringComparison.Ordinal);

        Assert.DoesNotContain("sk-test", savedJson, StringComparison.Ordinal);

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
