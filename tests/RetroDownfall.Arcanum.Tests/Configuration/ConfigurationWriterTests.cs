using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Configuration;

[Collection("ProcessEnvironment")]
public sealed class ConfigurationWriterTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private string? _backupConfigPath;

    private string? _originalHome;

    private string? _originalUserProfile;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _originalHome = global::System.Environment.GetEnvironmentVariable("HOME");

        _originalUserProfile = global::System.Environment.GetEnvironmentVariable("USERPROFILE");

        global::System.Environment.SetEnvironmentVariable("HOME", _workspace.Root);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _workspace.Root);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        if (File.Exists(configPath))
        {

            _backupConfigPath = Path.Combine(_workspace.Root, "arcanum.json.bak");

            File.Copy(configPath, _backupConfigPath, overwrite: true);

        }

    }

    public async Task DisposeAsync()
    {

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        if (_backupConfigPath is not null && File.Exists(_backupConfigPath))
        {

            File.Copy(_backupConfigPath, configPath, overwrite: true);

            File.Delete(_backupConfigPath);

        }
        else if (File.Exists(configPath))
        {

            File.Delete(configPath);

        }

        global::System.Environment.SetEnvironmentVariable("HOME", _originalHome);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task WriteAsync_persists_protected_settings_to_grimoire()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "openai", ApiKey = "sk-test" },
            ],
        };

        Result result = await writer.WriteAsync(settings, CancellationToken.None);

        Assert.True(result.IsSuccess);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        Assert.True(File.Exists(configPath));

        string json = await File.ReadAllTextAsync(configPath);

        Assert.Contains("openai", json, StringComparison.Ordinal);

        Assert.Contains("dp:v1:", json, StringComparison.Ordinal);

        Assert.DoesNotContain("sk-test", json, StringComparison.Ordinal);

    }

    [Fact]
    public async Task WriteAsync_emits_indented_json_with_default_values()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "openai", ApiKey = "sk-test" },
            ],
        };

        Result result = await writer.WriteAsync(settings, CancellationToken.None);

        Assert.True(result.IsSuccess);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        string json = await File.ReadAllTextAsync(configPath);

        Assert.Contains('\n', json);

        Assert.Contains("\"host\":", json, StringComparison.Ordinal);

        Assert.Contains("\"port\": 5001", json, StringComparison.Ordinal);

        Assert.Contains("\"server\":", json, StringComparison.Ordinal);

        Assert.Contains("\"pidFilePath\":", json, StringComparison.Ordinal);

    }

    [Fact]
    public async Task WriteAsync_reports_failure_when_atomic_replace_aborts()
    {

        ConfigurationWriter writer = CreateWriter();

        ArcanumSettings original = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "original", ApiKey = "sk-original" },
            ],
        };

        Assert.True((await writer.WriteAsync(original, CancellationToken.None)).IsSuccess);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        string aliasPath = Path.Combine(_workspace.Root, "arcanum-hard-link.json");

        Assert.True(HardLinkTestSupport.TryCreate(aliasPath, configPath));

        byte[] originalBytes = await File.ReadAllBytesAsync(configPath);

        ArcanumSettings replacement = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "replacement", ApiKey = "sk-replacement" },
            ],
        };

        Result result = await writer.WriteAsync(replacement, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(configPath));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(aliasPath));

    }

    private static ConfigurationWriter CreateWriter()
    {

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.ConfigurationWriterTests");

        ConfigurationSecretProtector secretProtector = new(provider);

        return new ConfigurationWriter(NullLogger<ConfigurationWriter>.Instance, secretProtector);

    }

}
