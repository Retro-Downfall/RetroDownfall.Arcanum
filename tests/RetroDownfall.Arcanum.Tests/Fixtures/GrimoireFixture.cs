using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Generated;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

public sealed class GrimoireFixture : IDisposable
{

    public const string TestApiKey = "test-key";

    public static bool SqlCipherAvailable { get; private set; }

    public static string SqlCipherUnavailableReason { get; private set; } = "SQLCipher not probed yet.";

    private readonly string _templatePath;

    private readonly string _templateSidecarPath;

    private readonly string _templateFingerprintPath;

    private readonly string _passphrase;

    private readonly ConcurrentBag<string> _copyPaths = new();

    static GrimoireFixture()
    {

        try
        {
            Batteries_V2.Init();

            string probePath = Path.Combine(Path.GetTempPath(), "arcanum-tests", $"probe-{Guid.NewGuid():N}.db");

            Directory.CreateDirectory(Path.GetDirectoryName(probePath)!);

            // Use a deterministic salt for the shared template so that a template created by
            // a previous test process is still openable by a new process. The KDF sidecar tests
            // exercise random salt generation separately.
            _saltStatic = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
            _passphraseStatic = GrimoireKeyDerivation.DerivePassphraseFromApiKey(TestApiKey, _saltStatic);

            {
                using SqliteConnection probe = new(new SqliteConnectionStringBuilder
                {
                    DataSource = probePath,
                    Password = _passphraseStatic,
                    Pooling = false,
                }.ToString());

                probe.Open();

                probe.Close();

            }

            File.Delete(probePath);

            if (File.Exists(probePath))
            {

                throw new IOException($"SQLCipher availability probe was not deleted: {probePath}");

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

    private static readonly string _passphraseStatic = null!;

    private static readonly byte[] _saltStatic = null!;

    public GrimoireFixture()
    {

        _passphrase = _passphraseStatic;

        string dir = Path.Combine(Path.GetTempPath(), "arcanum-tests", "grimoire-template");

        Directory.CreateDirectory(dir);

        _templatePath = Path.Combine(dir, "template-remediation-v1.db");

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
    /// Hashes every embedded Grimoire SQL migration script plus the canonical raw-SQL schema
    /// fingerprint so the cached template database (which persists across test process invocations
    /// under the OS temp directory) is rebuilt whenever either schema source changes.
    /// </summary>
    private static string ComputeSchemaFingerprint()
    {

        Assembly assembly = typeof(GrimoireSqlSchemaMigrator).Assembly;

        StringBuilder combined = new();

        combined.Append("WeaveSchemaInitializer:")
            .Append(WeaveSchemaInitializer.CanonicalSchemaFingerprint)
            .Append('\n');

        combined.Append("LexiconSchemaInitializer:")
            .Append(LexiconSchemaInitializer.CanonicalSchemaFingerprint)
            .Append('\n');

        combined.Append("GrimoireSqlSchemaMigrator:")
            .Append(GrimoireSqlSchemaMigrator.CanonicalMigrationOrder)
            .Append('\n');

        foreach (string resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal))
        {

            using Stream stream = assembly.GetManifestResourceStream(resourceName)!;

            using StreamReader reader = new(stream, Encoding.UTF8);

            combined.Append(resourceName).Append('\n').Append(reader.ReadToEnd()).Append("\n---\n");

        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined.ToString()));

        return Convert.ToHexString(hash);

    }

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

        await GrimoireSqlSchemaMigrator.ApplyPendingAsync(connection, cancellationToken).ConfigureAwait(false);

        // RAG Phase 1 — The Weave: mirrors GrimoireDatabaseBootstrapper.EnsureInitializedAsync, which
        // runs WeaveSchemaInitializer right after Grimoire's own migrations. Always creates
        // entry_embeddings (the BLOB source of truth), independent of whether sqlite-vec is available,
        // so DivinationServiceTests (managed-fallback path) and any future writer test have a real
        // table to work against instead of each test standing up its own ad hoc schema.
        await WeaveSchemaInitializer.EnsureSchemaAsync(
            connection,
            configuredDimensions: new EmbeddingIntegrationSettings().Dimensions,
            availability: new WeaveIndexAvailability(),
            logger: null,
            cancellationToken).ConfigureAwait(false);

        // The Lexicon — mirrors GrimoireDatabaseBootstrapper.EnsureLexiconSchemaAsync, which runs
        // LexiconSchemaInitializer right after The Weave's schema. Always creates lexicon_entries +
        // lexicon_fts so LexiconServiceTests and downstream injection tests have real tables.
        await LexiconSchemaInitializer.EnsureSchemaAsync(connection, logger: null, cancellationToken).ConfigureAwait(false);

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
