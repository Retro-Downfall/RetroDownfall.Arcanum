using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Services;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// A read that failed never acknowledges the on-disk bytes, so the store's stale-file guard must refuse
/// the next write instead of degrading to the live hash and overwriting the operator's configuration.
/// </summary>
[Collection("EnvVarSensitive")]
public sealed class ConfigurationStoreUnreadableTests : IDisposable
{

    private readonly string _originalHome;

    private readonly string _originalUserProfile;

    private readonly string _tempRoot;

    public ConfigurationStoreUnreadableTests()
    {

        _originalHome = global::System.Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        _originalUserProfile = global::System.Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        _tempRoot = Path.Combine(Path.GetTempPath(), $"compendium-unreadable-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        global::System.Environment.SetEnvironmentVariable("HOME", _tempRoot);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _tempRoot);

    }

    [Fact]
    public async Task Write_after_a_failed_read_is_rejected_and_leaves_the_file_untouched()
    {

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        const string original = "{ \"Arcanum\": { \"host\": { \"port\": 5001 }, ";

        await File.WriteAllTextAsync(configPath, original);

        using ArcanumConfigurationStore store = new(enableWatcher: false);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync());

        ConfigurationWriteResult result = await store.WriteAsync(new ArcanumSettings());

        Assert.False(result.IsSuccess);

        Assert.NotNull(result.ErrorMessage);

        Assert.Contains("could not be read", result.ErrorMessage, StringComparison.Ordinal);

        Assert.Equal(original, await File.ReadAllTextAsync(configPath));

    }

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable("HOME", _originalHome);

        global::System.Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);

        try
        {

            if (Directory.Exists(_tempRoot))
            {

                Directory.Delete(_tempRoot, recursive: true);

            }

        }
        catch
        {
            // best-effort cleanup
        }

    }

}
