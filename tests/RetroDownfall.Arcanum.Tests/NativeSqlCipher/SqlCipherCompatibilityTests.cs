using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// Runtime proof that the hermetic library encrypts, refuses the wrong key, supports the FTS5
/// configuration Covenant depends on, cannot load an extension, and still opens a Grimoire written
/// by the runtime Arcanum shipped before it.
/// </summary>
/// <remarks>
/// These assertions are deliberately behavioral. A SQLCipher build with the codec compiled out
/// accepts <c>PRAGMA key</c> and writes plaintext, and reports a perfectly correct
/// <c>cipher_version</c> while doing it, so version strings alone prove nothing.
/// </remarks>
public sealed class SqlCipherCompatibilityTests : IDisposable
{

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "arcanum-native-tests",
        Path.GetRandomFileName());

    public SqlCipherCompatibilityTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {

        try
        {

            if (Directory.Exists(_scratch))
            {

                Directory.Delete(_scratch, recursive: true);

            }

        }
        catch (IOException)
        {

            // A leftover scratch directory under the OS temp root is harmless.

        }

    }

    [Fact]
    public async Task ValidateAsync_accepts_the_pinned_runtime_and_codec()
    {

        SqliteNativeRuntimeValidationResult result = await Validator()
            .ValidateAsync(_scratch, CancellationToken.None);

        Assert.True(result.IsValid, result.ErrorCode);

        Assert.Equal("3.53.3", result.SqliteVersion);

        Assert.Equal("4.17.0 community", result.CipherVersion);

        Assert.True(result.CodecRoundTripPassed);

        Assert.True(result.CipherIntegrityPassed);

        Assert.True(result.AssetHashMatched);

        Assert.Empty(result.MissingCompileOptions);

    }

    [Fact]
    public async Task ValidateAsync_rejects_wrong_key_and_proves_fts_and_extension_policy()
    {

        SqliteNativeRuntimeValidationResult result = await Validator()
            .ValidateAsync(_scratch, CancellationToken.None);

        Assert.True(result.WrongKeyRejected);

        Assert.True(result.FtsSecureDeletePassed);

        Assert.True(result.LoadExtensionBlocked);

    }

    /// <summary>
    /// The validator writes a real encrypted database. Whatever the outcome, it must not leave the
    /// database or its WAL and shared-memory sidecars behind: those are the one place a scratch
    /// proof could leave recoverable material on disk.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_removes_every_scratch_artifact()
    {

        _ = await Validator().ValidateAsync(_scratch, CancellationToken.None);

        string[] leftovers = Directory.GetFiles(_scratch, "*", SearchOption.AllDirectories);

        Assert.True(
            leftovers.Length == 0,
            "Validation left scratch artifacts behind: "
            + string.Join(", ", leftovers.Select(Path.GetFileName)));

    }

    /// <summary>
    /// The reason old-database compatibility is not negotiable: an operator upgrading Arcanum keeps
    /// the Grimoire they already have. The fixture was written by the previous shipping runtime
    /// (SQLite 3.39.2) and must open, read, and accept writes under the new one.
    /// </summary>
    [Fact]
    public async Task Pinned_runtime_opens_and_mutates_the_legacy_compatibility_fixture()
    {

        (string databasePath, string key, string sentinel) = CopyLegacyFixture();

        SqliteNativeRuntime.Instance.Initialize();

        await using (SqliteConnection connection = Open(databasePath, key))
        {

            await connection.OpenAsync(CancellationToken.None);

            Assert.Equal(
                sentinel,
                await ScalarAsync(connection, "SELECT value FROM legacy_sentinel WHERE id = 1;"));

            // The new runtime must also be able to write to it, not merely read it.
            await ExecuteAsync(
                connection,
                "INSERT INTO legacy_sentinel (id, value) VALUES (2, 'written-by-the-hermetic-runtime');");

            Assert.Equal(
                "inherited corpus",
                await ScalarAsync(connection, "SELECT body FROM legacy_fts WHERE legacy_fts MATCH 'inherited';"));

        }

        SqliteConnection.ClearAllPools();

        await using (SqliteConnection reopened = Open(databasePath, key))
        {

            await reopened.OpenAsync(CancellationToken.None);

            Assert.Equal(
                "written-by-the-hermetic-runtime",
                await ScalarAsync(reopened, "SELECT value FROM legacy_sentinel WHERE id = 2;"));

        }

    }

    /// <summary>
    /// The complementary direction: a database created by the new runtime must refuse the wrong key
    /// and must not be readable as plaintext. A file whose header still says "SQLite format 3" was
    /// never encrypted at all.
    /// </summary>
    [Fact]
    public async Task New_runtime_database_is_encrypted_on_disk()
    {

        SqliteNativeRuntime.Instance.Initialize();

        string path = Path.Combine(_scratch, "encrypted-probe.db");

        await using (SqliteConnection connection = Open(path, "a-real-passphrase"))
        {

            await connection.OpenAsync(CancellationToken.None);

            await ExecuteAsync(connection, "CREATE TABLE secrets (value TEXT NOT NULL);");

            await ExecuteAsync(connection, "INSERT INTO secrets (value) VALUES ('plaintext-canary');");

            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");

        }

        SqliteConnection.ClearAllPools();

        byte[] raw = await File.ReadAllBytesAsync(path, CancellationToken.None);

        Assert.DoesNotContain("SQLite format 3"u8.ToArray(), Window(raw, 16));

        Assert.False(
            ContainsSequence(raw, "plaintext-canary"u8.ToArray()),
            "The row value is readable in the database file: this build is not encrypting.");

    }

    private static SqliteNativeRuntimeValidator Validator() => new(SqliteNativeRuntime.Instance);

    /// <summary>
    /// Copies the checked-in fixture to scratch. The committed file is never opened directly,
    /// because opening a SQLite database mutates it.
    /// </summary>
    private (string DatabasePath, string Key, string Sentinel) CopyLegacyFixture()
    {

        string directory = Path.Combine(
            NativeSqlCipherTestPaths.RepositoryRoot(),
            "tests",
            "RetroDownfall.Arcanum.Tests",
            "TestData",
            "SqlCipher");

        string sidecar = Assert.Single(Directory.GetFiles(directory, "sqlcipher-legacy-*.json"));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sidecar));

        JsonElement root = document.RootElement;

        string fileName = root.GetProperty("fileName").GetString()!;

        string source = Path.Combine(directory, fileName);

        Assert.True(File.Exists(source), $"Missing compatibility fixture: {source}");

        string destination = Path.Combine(_scratch, fileName);

        File.Copy(source, destination);

        return (
            destination,
            root.GetProperty("key").GetString()!,
            root.GetProperty("sentinel").GetString()!);

    }

    private static SqliteConnection Open(string path, string key) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,

            Password = key,

            Pooling = false,
        }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is null or DBNull ? null : Convert.ToString(value);

    }

    private static byte[] Window(byte[] source, int length) =>
        source.Length <= length ? source : source[..length];

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {

        if (needle.Length == 0 || haystack.Length < needle.Length)
        {

            return false;

        }

        for (int index = 0; index <= haystack.Length - needle.Length; index++)
        {

            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle))
            {

                return true;

            }

        }

        return false;

    }

}
