using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

[Collection("ProcessEnvironment")]
public sealed class GrimoireCliInitializationTests : IDisposable
{

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    private readonly string _testHome =
        Path.Combine(Path.GetTempPath(), "arcanum-tests", $"cli-initialization-{Guid.NewGuid():N}");

    public GrimoireCliInitializationTests()
    {

        Directory.CreateDirectory(_testHome);

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

    }

    [Fact]
    public async Task EnsureInitializedAsync_concurrent_and_repeated_calls_bootstrap_once()
    {

        GatedSecretStore secretStore = new("test-master-key", gateApiKeyRead: true);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>());

        Task first = initialization.EnsureInitializedAsync(CancellationToken.None);

        await secretStore.ApiKeyReadStarted.WaitAsync(TimeSpan.FromSeconds(10));

        Task concurrent = initialization.EnsureInitializedAsync(CancellationToken.None);

        Assert.False(concurrent.IsCompleted);

        secretStore.ReleaseApiKeyRead();

        await Task.WhenAll(first, concurrent);

        await initialization.EnsureInitializedAsync(CancellationToken.None);

        Assert.Equal(1, secretStore.ApiKeyReadCount);

        Assert.False(string.IsNullOrWhiteSpace(passphraseSource.Passphrase));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile + ".kdf"));

        Assert.True(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

    }

    [Fact]
    public async Task EnsureInitializedAsync_after_failure_releases_mutex_and_retries()
    {

        GatedSecretStore secretStore = new(apiKey: null, gateApiKeyRead: false);

        GrimoireDbPassphraseSource passphraseSource = new();

        await using ServiceProvider provider = CreateServices();

        GrimoireCliInitialization initialization = new(
            secretStore,
            passphraseSource,
            provider.GetRequiredService<IServiceScopeFactory>());

        await Assert.ThrowsAsync<MissingMasterApiKeyException>(
            () => initialization.EnsureInitializedAsync(CancellationToken.None));

        await Assert.ThrowsAsync<MissingMasterApiKeyException>(
            () => initialization.EnsureInitializedAsync(CancellationToken.None));

        Assert.Equal(2, secretStore.ApiKeyReadCount);

        Assert.False(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

    }

    public void Dispose()
    {

        SqliteConnection.ClearAllPools();

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        try
        {

            if (Directory.Exists(_testHome))
            {

                Directory.Delete(_testHome, recursive: true);

            }

        }
        catch
        {

            // Best-effort cleanup for the uniquely owned test root.

        }

    }

    private static ServiceProvider CreateServices()
    {

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        services.AddSingleton<WeaveIndexAvailability>();

        services.AddLogging();

        return services.BuildServiceProvider();

    }

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

    private sealed class GatedSecretStore(string? apiKey, bool gateApiKeyRead) : ISecretStore
    {

        private readonly TaskCompletionSource _apiKeyReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseApiKeyRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string? _encryptionSecret;

        private int _apiKeyReadCount;

        public Task ApiKeyReadStarted => _apiKeyReadStarted.Task;

        public int ApiKeyReadCount => Volatile.Read(ref _apiKeyReadCount);

        public async Task<string?> GetApiKeyAsync()
        {

            Interlocked.Increment(ref _apiKeyReadCount);

            _apiKeyReadStarted.TrySetResult();

            if (gateApiKeyRead)
            {

                await _releaseApiKeyRead.Task.ConfigureAwait(false);

            }

            return apiKey;

        }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                string.IsNullOrWhiteSpace(apiKey)
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string savedApiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult(_encryptionSecret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

            _encryptionSecret = encryptionSecret;

            return Task.CompletedTask;

        }

        public void ReleaseApiKeyRead() => _releaseApiKeyRead.TrySetResult();

    }

}
