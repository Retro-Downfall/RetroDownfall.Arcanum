using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class GrimoireFixture : IDisposable
{

    public const string TestApiKey = "test-key";

    /// <summary>
    /// Dedicated Grimoire encryption secret for the shared template. Production never keys a
    /// sidecar-backed database from the master API key (<c>CreateNewDatabaseSecretAsync</c>,
    /// <c>RekeyToPbkdf2Async</c>, and backup restore all pass a dedicated secret), and the
    /// bootstrapper now fails closed rather than falling back to the API key, so the fixture must
    /// model the same shape. <see cref="TestApiKeySecretStore"/> serves this value.
    /// </summary>
    public const string TestGrimoireSecret = "test-grimoire-encryption-secret";

    /// <summary>
    /// Directory holding the shared, cached template database.
    /// </summary>
    public static string TemplateDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "arcanum-tests", "grimoire-template");

    /// <summary>
    /// Path of the shared template database. Tests that reason about template remediation must read
    /// it from here rather than repeating the file name — <c>GrimoireFixtureConcurrencyTests</c> once
    /// hard-coded <c>v1</c> and silently stopped observing the fixture when the name moved to v2.
    /// </summary>
    /// <remarks>
    /// v2: the template is keyed from the dedicated Grimoire secret rather than the master API key.
    /// A distinct file name keeps a concurrently running older test process from thrashing the cached
    /// template it can no longer open.
    /// </remarks>
    public static string TemplatePath { get; } =
        Path.Combine(TemplateDirectory, "template-remediation-v2.db");

    public static bool SqlCipherAvailable { get; private set; }

    public static string SqlCipherUnavailableReason { get; private set; } = "SQLCipher not probed yet.";

    private readonly string _templatePath;

    private readonly string _templateSidecarPath;

    private readonly string _templateFingerprintPath;

    private readonly string _passphrase;

    private readonly ConcurrentBag<string> _copyPaths = new();

    static GrimoireFixture()
    {

        // Use a deterministic salt for the shared template so that a template created by
        // a previous test process is still openable by a new process. The KDF sidecar tests
        // exercise random salt generation separately.
        _saltStatic = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        _passphraseStatic = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
            TestGrimoireSecret,
            _saltStatic);

        string probePath = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"probe-{Guid.NewGuid():N}.db");

        (bool available, string reason) = ProbeSqlCipher(probePath, _passphraseStatic);

        SqlCipherAvailable = available;

        SqlCipherUnavailableReason = reason;

    }

    /// <summary>
    /// Probes for a usable SQLCipher native library.
    /// </summary>
    /// <remarks>
    /// This runs from the static constructor, so it must never throw: an escaping exception marks
    /// this type's initializer as failed permanently, and every later read of
    /// <see cref="SqlCipherAvailable"/> — roughly sixty test classes reach it through
    /// <c>Skip.IfNot</c> — would then throw <see cref="TypeInitializationException"/> instead of
    /// skipping. Any failure is therefore reported as unavailable with its own message.
    /// </remarks>
    internal static (bool Available, string Reason) ProbeSqlCipher(string probePath, string passphrase)
    {

        try
        {
            Batteries_V2.Init();

            Directory.CreateDirectory(Path.GetDirectoryName(probePath)!);

            {
                using SqliteConnection probe = new(new SqliteConnectionStringBuilder
                {
                    DataSource = probePath,
                    Password = passphrase,
                    Pooling = false,
                }.ToString());

                probe.Open();

                probe.Close();

            }

            TryDeleteProbe(probePath);

            return (true, string.Empty);

        }
        catch (Exception ex)
        {

            return (
                false,
                $"SQLCipher availability probe failed ({ex.GetType().Name}): {ex.Message}. "
                + "Install e_sqlcipher runtimes or run on a supported RID.");

        }

    }

    /// <summary>
    /// Removes the probe database on a best-effort basis. The probe already proved SQLCipher works
    /// by opening an encrypted connection, so a scanner or indexer still holding the handle must
    /// neither fail the probe nor throw out of the static constructor.
    /// </summary>
    private static void TryDeleteProbe(string probePath)
    {

        try
        {

            File.Delete(probePath);

        }
        catch
        {

            // Best-effort cleanup of the probe database.

        }

    }

    private static readonly string _passphraseStatic = null!;

    private static readonly byte[] _saltStatic = null!;

    public GrimoireFixture()
    {

        _passphrase = _passphraseStatic;

        Directory.CreateDirectory(TemplateDirectory);

        _templatePath = TemplatePath;

        _templateSidecarPath = _templatePath + ".kdf";

        _templateFingerprintPath = _templatePath + ".fingerprint";

        if (!SqlCipherAvailable)
        {
            return;
        }

        lock (BuildLock)
        {
            using IDisposable processLock = AcquireCrossProcessTemplateLock();

            string currentFingerprint = ComputeSchemaFingerprint();

            if (File.Exists(_templatePath)
                && File.Exists(_templateSidecarPath)
                && File.Exists(_templateFingerprintPath)
                && File.ReadAllText(_templateFingerprintPath) == currentFingerprint
                && CanOpenTemplateAsync(_templatePath, _passphrase, CancellationToken.None).GetAwaiter().GetResult())
            {
                return;
            }

            DeleteTemplateFiles();
            BuildTemplateAsync(CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText(_templateFingerprintPath, currentFingerprint);

        }

    }

    /// <summary>
    /// Identity of the canonical schema source, so the cached template database (which persists
    /// across test process invocations under the OS temp directory) is rebuilt whenever any object
    /// file in <c>Data/Schema/</c> changes. One declarative tree means one fingerprint.
    /// </summary>
    private static string ComputeSchemaFingerprint() =>
        GrimoireSchemaCatalog.CanonicalSchemaFingerprint;

    public string Passphrase => _passphrase;

    private static readonly object BuildLock = new();

    private static readonly Mutex CrossProcessTemplateLock = new(
        initiallyOwned: false,
        name: "RetroDownfall.Arcanum.Tests.GrimoireTemplate.v1");

    private static IDisposable AcquireCrossProcessTemplateLock()
    {
        try
        {
            if (!CrossProcessTemplateLock.WaitOne(TimeSpan.FromMinutes(2)))
            {
                throw new TimeoutException(
                    "Timed out waiting for the cross-process Grimoire template lock.");
            }
        }
        catch (AbandonedMutexException)
        {
            // The prior process exited while owning the mutex. This process now owns it and
            // validates/remediates the template before returning any copy.
        }

        return new CrossProcessMutexLease();
    }

    public string CopyDatabase()
    {

        string copyPath = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"grimoire-{Guid.NewGuid():N}.db");

        string copySidecarPath = copyPath + ".kdf";

        _copyPaths.Add(copyPath);

        _copyPaths.Add(copySidecarPath);

        lock (BuildLock)
        {
            using IDisposable processLock = AcquireCrossProcessTemplateLock();

            try
            {

                File.Copy(_templatePath, copyPath, overwrite: true);

                File.Copy(_templateSidecarPath, copySidecarPath, overwrite: true);

                return copyPath;

            }
            catch
            {

                File.Delete(copyPath);

                File.Delete(copySidecarPath);

                throw;

            }

        }

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
                Pooling = false,
            }.ToString())
            .UseModel(ArcanumDbContextModel.Instance)
            .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance)
            .Options;

        ArcanumDbContext context = new(options, secretStore, passphraseSource);

        // Keep the non-pooled SQLCipher connection open for the context lifetime. Reopening an
        // encrypted database for every EF operation is prohibitively slow on Windows because each
        // open repeats key derivation; disposing the context still releases the file handle.
        context.Database.OpenConnection();

        return context;

    }

    public IOptionsMonitor<ArcanumSettings> CreateOptionsMonitor() =>
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings());

    private async Task BuildTemplateAsync(CancellationToken cancellationToken)
    {

        DeleteTemplateFiles();

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _templatePath,
            Password = _passphrase,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Mirrors GrimoireDatabaseBootstrapper: one installer creates the complete schema — Grimoire
        // core tables, FTS, triggers, The Weave/Saga/Tapestry BLOB stores, and The Lexicon — so every
        // test works against the same schema the host installs instead of an ad hoc subset.
        _ = await GrimoireSchemaInstaller.InstallAsync(
            connection,
            embeddingDimensions: new EmbeddingIntegrationSettings().Dimensions,
            logger: null,
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand checkpoint = connection.CreateCommand();

        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

        _ = await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await connection.CloseAsync().ConfigureAwait(false);

        GrimoireKdfSidecar sidecar = new()
        {
            Version = GrimoireKeyDerivation.KdfVersion2,
            SaltBase64 = Convert.ToBase64String(_saltStatic),
        };

        GrimoireKdfSidecarFile.Write(_templatePath, sidecar);

    }

    private void DeleteTemplateFiles()
    {

        string[] suffixes = ["", "-wal", "-shm", ".kdf", ".fingerprint"];

        foreach (string suffix in suffixes)
        {

            try
            {

                string path = _templatePath + suffix;

                if (File.Exists(path))
                {

                    File.Delete(path);

                }

            }
            catch
            {

                // Best-effort cleanup of stale template files.

            }

        }

    }

    private static async Task<bool> CanOpenTemplateAsync(string templatePath, string passphrase, CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteConnection probe = new(new SqliteConnectionStringBuilder
            {
                DataSource = templatePath,
                Password = passphrase,
                Pooling = false,
            }.ToString());

            await probe.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return true;

        }
        catch
        {

            return false;

        }

    }

    public void Dispose()
    {

        foreach (string copyPath in _copyPaths)
        {

            try
            {

                if (File.Exists(copyPath))
                {

                    File.Delete(copyPath);

                }

            }
            catch
            {

                // Best-effort cleanup; another test may still hold the handle.

            }

        }

    }

    private sealed class CrossProcessMutexLease : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CrossProcessTemplateLock.ReleaseMutex();
        }
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
