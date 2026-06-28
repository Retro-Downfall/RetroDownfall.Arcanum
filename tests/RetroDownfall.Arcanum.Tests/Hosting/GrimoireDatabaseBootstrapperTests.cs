using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class GrimoireDatabaseBootstrapperTests : IDisposable
{

    private readonly string _tempDir;

    private readonly string _dbPath;

    private readonly string _sidecarPath;

    private readonly TestSecretStore _secretStore;

    private readonly GrimoireDbPassphraseSource _passphraseSource;

    private readonly IServiceScopeFactory _scopeFactory;

    public GrimoireDatabaseBootstrapperTests()
    {

        Batteries_V2.Init();

        _tempDir = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"bootstrapper-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempDir);

        _dbPath = Path.Combine(_tempDir, "grimoire.db");

        _sidecarPath = _dbPath + ".kdf";

        _secretStore = new TestSecretStore();

        _passphraseSource = new GrimoireDbPassphraseSource();

        ServiceCollection services = new();

        services.AddSingleton<IGrimoireDbReadiness, GrimoireDbReadiness>();

        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    [Fact]
    public async Task EnsureInitializedAsync_missing_api_key_throws_MissingMasterApiKeyException()
    {

        // No API key set on the secret store -> GetApiKeyAsync returns null/whitespace,
        // which must surface as a recoverable MissingMasterApiKeyException (not Environment.FailFast).

        MissingMasterApiKeyException ex = await Assert.ThrowsAsync<MissingMasterApiKeyException>(() =>
            GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
                _secretStore,
                _passphraseSource,
                _scopeFactory,
                _dbPath,
                _tempDir,
                CancellationToken.None));

        Assert.Equal(MissingMasterApiKeyException.MessageText, ex.Message);

    }

    [Fact]
    public async Task EnsureInitializedAsync_NewDatabase_CreatesSidecarAndUsesPbkdf2()
    {

        _secretStore.SetApiKey("test-api-key");

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_dbPath));

        Assert.True(File.Exists(_sidecarPath));

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(_dbPath);

        Assert.Equal(GrimoireKeyDerivation.KdfVersion2, sidecar.Version);

        Assert.NotNull(_secretStore.DedicatedSecret);

    }

    [Fact]
    public async Task EnsureInitializedAsync_LegacyApiKeyDatabase_UpgradesToPbkdf2()
    {

        _secretStore.SetApiKey("legacy-api-key");

        string legacyPassphrase = GrimoireKeyDerivation.DerivePassphraseFromApiKeyLegacy("legacy-api-key");

        await CreateLegacyDatabaseAsync(legacyPassphrase);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_sidecarPath));

        Assert.NotNull(_secretStore.DedicatedSecret);

        await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = _passphraseSource.Passphrase,
        }.ToString());

        await probe.OpenAsync();

        await using SqliteCommand cmd = probe.CreateCommand();

        cmd.CommandText = "SELECT 1;";

        _ = await cmd.ExecuteScalarAsync();

    }

    [Fact]
    public async Task EnsureInitializedAsync_LegacyDedicatedSecretDatabase_UpgradesToPbkdf2()
    {

        _secretStore.SetApiKey("legacy-api-key");

        string dedicatedSecret = "dedicated-legacy-secret";

        _secretStore.SetDedicatedSecret(dedicatedSecret);

        string legacyPassphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecretLegacy(dedicatedSecret);

        await CreateLegacyDatabaseAsync(legacyPassphrase);

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            _secretStore,
            _passphraseSource,
            _scopeFactory,
            _dbPath,
            _tempDir,
            CancellationToken.None);

        Assert.True(File.Exists(_sidecarPath));

        await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = _passphraseSource.Passphrase,
        }.ToString());

        await probe.OpenAsync();

        await using SqliteCommand cmd = probe.CreateCommand();

        cmd.CommandText = "SELECT 1;";

        _ = await cmd.ExecuteScalarAsync();

    }

    private async Task CreateLegacyDatabaseAsync(string passphrase)
    {

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Password = passphrase,
        }.ToString());

        await connection.OpenAsync();

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, CancellationToken.None);

        await connection.CloseAsync();

    }

    public void Dispose()
    {

        try
        {

            if (Directory.Exists(_tempDir))
            {

                Directory.Delete(_tempDir, recursive: true);

            }

        }
        catch
        {

            // Best-effort cleanup.

        }

    }

    private sealed class GrimoireDbReadiness : IGrimoireDbReadiness
    {

        public bool IsReady { get; private set; }

        public void MarkReady() => IsReady = true;

    }

    private sealed class TestSecretStore : ISecretStore
    {

        public string? ApiKey { get; private set; }

        public string? DedicatedSecret { get; private set; }

        public void SetApiKey(string apiKey) => ApiKey = apiKey;

        public void SetDedicatedSecret(string secret) => DedicatedSecret = secret;

        public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(ApiKey is null ? SecretStoreReadResult.Missing() : SecretStoreReadResult.Ok(ApiKey));

        public Task SaveApiKeyAsync(string apiKey)
        {

            ApiKey = apiKey;

            return Task.CompletedTask;

        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult(DedicatedSecret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

            DedicatedSecret = encryptionSecret;

            return Task.CompletedTask;

        }

    }

}
