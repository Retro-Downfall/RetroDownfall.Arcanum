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
    /// Root under which every test process keeps its own template directory.
    /// </summary>
    private static string TemplateRoot { get; } =
        Path.Combine(Path.GetTempPath(), "arcanum-tests");

    /// <summary>
    /// Directory holding this process's cached template database.
    /// </summary>
    /// <remarks>
    /// Private to the process, not shared machine-wide. The template lifecycle deletes and rebuilds
    /// files in place (fingerprint mismatch, and <c>Concurrent_template_rebuild_and_copies_produce_complete_databases</c>
    /// deletes the fingerprint outright), so a single machine-global directory let any two test
    /// processes sharing a temp directory destroy each other's template mid-copy — one run of that
    /// produced ~158 "file being used by another process" and missing-<c>.db.kdf</c> failures spread
    /// across suites unrelated to either change. The caching benefit that matters is within a run:
    /// every collection fixture in this process still shares one built template.
    /// </remarks>
    public static string TemplateDirectory { get; } = Path.Combine(
        TemplateRoot,
        "grimoire-template-" + global::System.Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Path of this process's template database. Tests that reason about template remediation must read
    /// it from here rather than repeating the file name — <c>GrimoireFixtureConcurrencyTests</c> once
    /// hard-coded <c>v1</c> and silently stopped observing the fixture when the name moved to v2.
    /// </summary>
    /// <remarks>
    /// v2: the template is keyed from the dedicated Grimoire secret rather than the master API key.
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

        // Use a deterministic salt for the template so every fixture instance in this process derives
        // the same passphrase without repeating the KDF. The KDF sidecar tests exercise random salt
        // generation separately.
        _saltStatic = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

        _passphraseStatic = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
            TestGrimoireSecret,
            _saltStatic);

        SweepAbandonedTemplateDirectories();

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => DeleteTemplateDirectory();

        string probePath = Path.Combine(TemplateRoot, $"probe-{Guid.NewGuid():N}.db");

        (bool available, string reason) = ProbeSqlCipher(probePath, _passphraseStatic);

        SqlCipherAvailable = available;

        SqlCipherUnavailableReason = reason;

    }

    /// <summary>
    /// Removes this process's template directory on the way out. Per-process directories would
    /// otherwise accumulate one tree per run under the temp directory.
    /// </summary>
    private static void DeleteTemplateDirectory()
    {

        try
        {

            if (Directory.Exists(TemplateDirectory))
            {

                Directory.Delete(TemplateDirectory, recursive: true);

            }

        }
        catch
        {

            // Best-effort: a crashed run's directory is collected by the sweep below instead.

        }

    }

    /// <summary>
    /// Best-effort collection of template directories left behind by test processes that were killed
    /// before their <see cref="AppDomain.ProcessExit"/> handler ran. Only directories older than the
    /// grace period are touched, so a concurrently running process never loses its live template.
    /// </summary>
    private static void SweepAbandonedTemplateDirectories()
    {

        try
        {

            if (!Directory.Exists(TemplateRoot))
            {

                return;

            }

            DateTime cutoff = DateTime.UtcNow - TimeSpan.FromHours(12);

            foreach (string directory in Directory.EnumerateDirectories(TemplateRoot, "grimoire-template-*"))
            {

                try
                {

                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {

                        Directory.Delete(directory, recursive: true);

                    }

                }
                catch
                {

                    // Another process may own it, or it may have vanished between enumeration and delete.

                }

            }

        }
        catch
        {

            // The sweep is an optimisation; never let it fail the static initializer.

        }

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
            SqliteNativeRuntime.Instance.Initialize();

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

    /// <summary>
    /// Serialises template build and copy. The name carries this process's template directory token so
    /// two concurrent test processes never queue behind one another on a machine-global name — each
    /// owns a private template and has nothing to serialise against the other.
    /// </summary>
    private static readonly Mutex CrossProcessTemplateLock = new(
        initiallyOwned: false,
        name: $"RetroDownfall.Arcanum.Tests.GrimoireTemplate.{Path.GetFileName(TemplateDirectory)}");

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

    /// <summary>
    /// Every file a handed-out copy can put on disk, relative to its database path. The copies are
    /// opened in <c>journal_mode=WAL</c> (see <see cref="SqlitePragmaConnectionInterceptor"/>), and
    /// the <c>-wal</c>/<c>-shm</c> pair outlives the connection, so cleanup that only knew about the
    /// database and its KDF sidecar left two orphans under the temp directory per copy.
    /// </summary>
    private static readonly string[] CopySuffixes = ["", "-wal", "-shm", ".kdf"];

    public string CopyDatabase()
    {

        string copyPath = Path.Combine(TemplateRoot, $"grimoire-{Guid.NewGuid():N}.db");

        string copySidecarPath = copyPath + ".kdf";

        _copyPaths.Add(copyPath);

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

                DeleteCopyFiles(copyPath);

                throw;

            }

        }

    }

    private static void DeleteCopyFiles(string copyPath)
    {

        foreach (string suffix in CopySuffixes)
        {

            try
            {

                string path = copyPath + suffix;

                if (File.Exists(path))
                {

                    File.Delete(path);

                }

            }
            catch
            {

                // Best-effort cleanup; another test may still hold the handle.

            }

        }

    }

    /// <summary>
    /// A context over one database copy, unpooled by default.
    /// </summary>
    /// <remarks>
    /// <paramref name="pooled"/> is the shape the host composes in production, where nothing sets
    /// <c>Pooling</c> and the provider's default therefore applies. A suite asking a question about
    /// what a connection leaves behind has to be able to ask it of that shape: a pooled handle is not
    /// closed by disposal, so the sidecars outlive the caller, and a fixture that only ever composed
    /// the unpooled shape would answer every such question with the reassuring one.
    /// </remarks>
    public ArcanumDbContext CreateContext(string databasePath, bool pooled = false)
    {

        TestGrimoireDbPassphraseSource passphraseSource = new();

        passphraseSource.SetPassphrase(_passphrase);

        TestSecretStore secretStore = new();

        DbContextOptions<ArcanumDbContext> options = new DbContextOptionsBuilder<ArcanumDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Password = _passphrase,
                Pooling = pooled,
            }.ToString())
            .UseModel(ArcanumDbContextModel.Instance)
            .AddInterceptors(SqlitePragmaConnectionInterceptor.Instance)
            .Options;

        ArcanumDbContext context = new(options, secretStore, passphraseSource);

        // Keep the SQLCipher connection open for the context lifetime. Reopening an encrypted
        // database for every EF operation is prohibitively slow on Windows because each open repeats
        // key derivation; disposing the context still releases the file handle.
        context.Database.OpenConnection();

        return context;

    }

    public IOptionsMonitor<ArcanumSettings> CreateOptionsMonitor() =>
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings());

    private async Task BuildTemplateAsync(CancellationToken cancellationToken)
    {

        DeleteTemplateFiles();

        // Opened through the central initializer rather than raw, exactly as the host bootstrapper
        // does. The core schema's guard triggers call the arcanum_*_authorized scalar functions the
        // initializer registers, so a raw connection fails an authorized seed with "no such
        // function" instead of allowing or denying it.
        await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
            new SqliteConnectionStringBuilder
            {
                DataSource = _templatePath,
                Password = _passphrase,
                Pooling = false,
            }.ToString(),
            cancellationToken).ConfigureAwait(false);

        // Mirrors GrimoireDatabaseBootstrapper: one installer creates the complete schema — Grimoire
        // core tables, FTS, triggers, The Weave/Saga/Tapestry BLOB stores, and The Lexicon — so every
        // test works against the same schema the host installs instead of an ad hoc subset.
        _ = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            embeddingDimensions: new EmbeddingIntegrationSettings().Dimensions,
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand checkpoint = connection.CreateCommand();

        // Checkpoint, then leave the template in rollback-journal mode. The initializer switches
        // every connection it touches to WAL, and journal mode is persisted in the file header, so a
        // WAL template would hand every copy a database that was already WAL before the copy's own
        // connection converted it — which is exactly the state the sidecar-leak regressions exist to
        // reproduce, and they can only reproduce it if the copy performs the conversion itself.
        checkpoint.CommandText = """
            PRAGMA wal_checkpoint(TRUNCATE);
            PRAGMA journal_mode=DELETE;
            """;

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

            DeleteCopyFiles(copyPath);

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
