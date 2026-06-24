using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class GrimoireFixture : IDisposable
{

    public const string TestApiKey = "test-key";

    public static bool SqlCipherAvailable { get; private set; }

    public static string SqlCipherUnavailableReason { get; private set; } = "SQLCipher not probed yet.";

    private readonly string _templatePath;

    private readonly string _passphrase;

    static GrimoireFixture()
    {

        try
        {
            Batteries_V2.Init();

            string probePath = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"probe-{Guid.NewGuid():N}.db");

            Directory.CreateDirectory(Path.GetDirectoryName(probePath)!);

            _passphraseStatic = GrimoireKeyDerivation.DerivePassphraseFromApiKey(TestApiKey);

            using SqliteConnection probe = new(new SqliteConnectionStringBuilder
            {
                DataSource = probePath,
                Password = _passphraseStatic,
            }.ToString());

            probe.Open();

            probe.Close();

            try
            {
                File.Delete(probePath);
            }
            catch
            {
            }

            SqlCipherAvailable = true;

            SqlCipherUnavailableReason = string.Empty;

        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            SqlCipherAvailable = false;

            SqlCipherUnavailableReason =
                $"SQLCipher native library unavailable: {ex.Message}. Install e_sqlcipher runtimes or run on a supported RID.";
        }

    }

    private static readonly string _passphraseStatic;

    public GrimoireFixture()
    {

        _passphrase = _passphraseStatic;

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-tests", "grimoire-template");

        Directory.CreateDirectory(dir);

        _templatePath = Path.Combine(dir, "template-remediation-v1.db");

        if (!SqlCipherAvailable)
        {
            return;
        }

        if (File.Exists(_templatePath))
        {
            return;
        }

        BuildTemplateAsync(CancellationToken.None).GetAwaiter().GetResult();

    }

    public string Passphrase => _passphrase;

    private static readonly object CopyLock = new();

    public string CopyDatabase()
    {

        string copyPath = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"grimoire-{Guid.NewGuid():N}.db");

        lock (CopyLock)
        {

            const int maxAttempts = 5;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {

                try
                {

                    File.Copy(_templatePath, copyPath, overwrite: true);

                    return copyPath;

                }
                catch (IOException) when (attempt < maxAttempts - 1)
                {

                    Thread.Sleep(50 * (attempt + 1));

                }

            }

        }

        File.Copy(_templatePath, copyPath, overwrite: true);

        return copyPath;

    }

    public ArcanumDbContext CreateContext(string databasePath)
    {

        TestGrimoireDbPassphraseSource passphraseSource = new();

        passphraseSource.SetPassphrase(_passphrase);

        TestSecretStore secretStore = new();

        DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Password = _passphrase,
            }.ToString())
            .UseModel(ArcanumDbContextModel.Instance)
            .Options;

        return new ArcanumDbContext(options, secretStore, passphraseSource);

    }

    public IOptionsMonitor<ArcanumSettings> CreateOptionsMonitor() =>
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings());

    private async Task BuildTemplateAsync(CancellationToken cancellationToken)
    {

        if (File.Exists(_templatePath))
        {
            File.Delete(_templatePath);
        }

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _templatePath,
            Password = _passphrase,
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand checkpoint = connection.CreateCommand();

        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

        _ = await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await connection.CloseAsync().ConfigureAwait(false);

    }

    public void Dispose()
    {
    }

    private sealed class TestGrimoireDbPassphraseSource : IGrimoireDbPassphraseSource
    {

        private string? _passphrase;

        public string Passphrase =>
            _passphrase
            ?? throw new InvalidOperationException("Grimoire database passphrase has not been initialized.");

        public void SetPassphrase(string passphrase)
        {

            ArgumentException.ThrowIfNullOrEmpty(passphrase);

            _passphrase = passphrase;

        }

    }

    private sealed class TestSecretStore : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

}
