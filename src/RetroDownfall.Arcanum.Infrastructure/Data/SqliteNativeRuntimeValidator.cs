using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Proves the loaded SQLCipher library actually behaves the way the manifest claims, before the
/// Grimoire is opened.
/// </summary>
/// <remarks>
/// A library can load, report the right version string, and still not encrypt — a build with the
/// codec compiled out accepts <c>PRAGMA key</c> silently and writes plaintext pages. So this does
/// not read version strings and call it verified: it creates a real encrypted database, closes it,
/// reopens it with the right key, proves a wrong key is refused, exercises FTS5 secure-delete, and
/// confirms extension loading is unavailable.
///
/// On any mismatch the Grimoire is unavailable. There is no second library to try.
/// </remarks>
internal sealed class SqliteNativeRuntimeValidator
{

    /// <summary>
    /// Compile options whose absence changes Covenant's security or search behavior. Compared
    /// against <c>PRAGMA compile_options</c>, which reports them without the <c>SQLITE_</c> prefix.
    /// </summary>
    private static readonly string[] RequiredPragmaCompileOptions =
    [
        "ENABLE_FTS5",

        "ENABLE_COLUMN_METADATA",

        "HAS_CODEC",

        "OMIT_LOAD_EXTENSION",

        "TEMP_STORE=2",

        "THREADSAFE=1",
    ];

    private readonly ISqliteNativeRuntime _nativeRuntime;

    internal SqliteNativeRuntimeValidator(ISqliteNativeRuntime nativeRuntime) =>
        _nativeRuntime = nativeRuntime;

    internal async Task<SqliteNativeRuntimeValidationResult> ValidateAsync(
        string scratchDirectory,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);

        SqliteNativeRuntimeManifest manifest;

        try
        {

            _nativeRuntime.Initialize();

            manifest = SqliteNativeRuntimeManifest.Load();

        }
        catch (SqliteNativeRuntimeUnavailableException)
        {

            return SqliteNativeRuntimeValidationResult.Failure("Grimoire.NativeRuntimeUnavailable");

        }
        catch (InvalidOperationException)
        {

            return SqliteNativeRuntimeValidationResult.Failure(
                SqliteNativeRuntimeManifest.InvalidManifestErrorCode);

        }

        bool assetHashMatched = manifest.TryVerifyDeliveredAsset(out _);

        using SqliteScratchDatabase scratch = new(scratchDirectory, "runtime-validation.db");

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        string wrongKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        try
        {

            string sqliteVersion;

            string cipherVersion;

            string cipherProvider;

            string cipherProviderVersion;

            List<string> missingCompileOptions;

            await using (SqliteConnection connection = Open(scratch.Path_, key))
            {

                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                sqliteVersion = await ScalarAsync(connection, "SELECT sqlite_version();", cancellationToken)
                    .ConfigureAwait(false) ?? string.Empty;

                cipherVersion = await ScalarAsync(connection, "PRAGMA cipher_version;", cancellationToken)
                    .ConfigureAwait(false) ?? string.Empty;

                cipherProvider = await ScalarAsync(connection, "PRAGMA cipher_provider;", cancellationToken)
                    .ConfigureAwait(false) ?? string.Empty;

                cipherProviderVersion = await ScalarAsync(
                    connection,
                    "PRAGMA cipher_provider_version;",
                    cancellationToken).ConfigureAwait(false) ?? string.Empty;

                missingCompileOptions = await MissingCompileOptionsAsync(connection, cancellationToken)
                    .ConfigureAwait(false);

                await ExecuteAsync(
                    connection,
                    "CREATE TABLE sentinel (id INTEGER PRIMARY KEY, value TEXT NOT NULL);",
                    cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(
                    connection,
                    "INSERT INTO sentinel (id, value) VALUES (1, 'arcanum');",
                    cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken)
                    .ConfigureAwait(false);

            }

            // Pooling is off, but the provider still caches at the native layer; clearing makes the
            // reopen below a genuine second open rather than a handle handed back.
            SqliteConnection.ClearAllPools();

            if (!string.Equals(sqliteVersion, manifest.SqliteVersion, StringComparison.Ordinal))
            {

                return SqliteNativeRuntimeValidationResult.Failure("Grimoire.NativeRuntimeVersionMismatch");

            }

            if (!string.Equals(cipherVersion, manifest.CipherVersion, StringComparison.Ordinal))
            {

                return SqliteNativeRuntimeValidationResult.Failure("Grimoire.NativeRuntimeCipherMismatch");

            }

            if (!string.Equals(cipherProvider, manifest.CipherProvider, StringComparison.OrdinalIgnoreCase))
            {

                return SqliteNativeRuntimeValidationResult.Failure("Grimoire.NativeRuntimeCipherProviderMismatch");

            }

            if (missingCompileOptions.Count > 0)
            {

                return new SqliteNativeRuntimeValidationResult
                {
                    IsValid = false,

                    ErrorCode = "Grimoire.NativeRuntimeCompileOptionsMismatch",

                    MissingCompileOptions = missingCompileOptions,
                };

            }

            bool codecRoundTrip = await CodecRoundTripsAsync(scratch.Path_, key, cancellationToken)
                .ConfigureAwait(false);

            bool integrityPassed = await CipherIntegrityPassesAsync(scratch.Path_, key, cancellationToken)
                .ConfigureAwait(false);

            bool wrongKeyRejected = await WrongKeyIsRejectedAsync(scratch.Path_, wrongKey, cancellationToken)
                .ConfigureAwait(false);

            bool ftsPassed = await FtsSecureDeletePassesAsync(scratch.Path_, key, cancellationToken)
                .ConfigureAwait(false);

            bool loadExtensionBlocked = await LoadExtensionIsBlockedAsync(scratch.Path_, key, cancellationToken)
                .ConfigureAwait(false);

            bool valid = codecRoundTrip
                && integrityPassed
                && wrongKeyRejected
                && ftsPassed
                && loadExtensionBlocked
                && assetHashMatched;

            return new SqliteNativeRuntimeValidationResult
            {
                IsValid = valid,

                ErrorCode = valid ? null : FirstFailure(
                    codecRoundTrip,
                    integrityPassed,
                    wrongKeyRejected,
                    ftsPassed,
                    loadExtensionBlocked,
                    assetHashMatched),

                SqliteVersion = sqliteVersion,

                CipherVersion = cipherVersion,

                CipherProvider = cipherProvider,

                CipherProviderVersion = cipherProviderVersion,

                CodecRoundTripPassed = codecRoundTrip,

                CipherIntegrityPassed = integrityPassed,

                WrongKeyRejected = wrongKeyRejected,

                FtsSecureDeletePassed = ftsPassed,

                LoadExtensionBlocked = loadExtensionBlocked,

                AssetHashMatched = assetHashMatched,
            };

        }
        catch (SqliteException)
        {

            return SqliteNativeRuntimeValidationResult.Failure("Grimoire.NativeRuntimeValidationFailed");

        }
        finally
        {

            SqliteConnection.ClearAllPools();

        }

    }

    private static string FirstFailure(
        bool codecRoundTrip,
        bool integrityPassed,
        bool wrongKeyRejected,
        bool ftsPassed,
        bool loadExtensionBlocked,
        bool assetHashMatched) => (codecRoundTrip, integrityPassed, wrongKeyRejected, ftsPassed, loadExtensionBlocked, assetHashMatched) switch
        {
            (false, _, _, _, _, _) => "Grimoire.NativeRuntimeCodecFailed",

            (_, false, _, _, _, _) => "Grimoire.NativeRuntimeCipherIntegrityFailed",

            (_, _, false, _, _, _) => "Grimoire.NativeRuntimeWrongKeyAccepted",

            (_, _, _, false, _, _) => "Grimoire.NativeRuntimeFtsFailed",

            (_, _, _, _, false, _) => "Grimoire.NativeRuntimeLoadExtensionAvailable",

            _ => "Grimoire.NativeRuntimeAssetHashMismatch",
        };

    /// <summary>
    /// Reopens the database written above and requires the sentinel to come back. This is what
    /// separates a real codec from one that accepted the key and ignored it.
    /// </summary>
    private static async Task<bool> CodecRoundTripsAsync(
        string path,
        string key,
        CancellationToken cancellationToken)
    {

        await using SqliteConnection connection = Open(path, key);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        string? value = await ScalarAsync(
            connection,
            "SELECT value FROM sentinel WHERE id = 1;",
            cancellationToken).ConfigureAwait(false);

        return string.Equals(value, "arcanum", StringComparison.Ordinal);

    }

    private static async Task<bool> CipherIntegrityPassesAsync(
        string path,
        string key,
        CancellationToken cancellationToken)
    {

        await using SqliteConnection connection = Open(path, key);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA cipher_integrity_check;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        // SQLCipher reports problems as rows and returns nothing when the database is intact, so an
        // empty result is the pass condition and any row must say ok.
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {

                return false;

            }

        }

        return true;

    }

    /// <summary>
    /// A wrong key must fail at the first page read, not at some later query.
    /// </summary>
    private static async Task<bool> WrongKeyIsRejectedAsync(
        string path,
        string wrongKey,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteConnection connection = Open(path, wrongKey);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            _ = await ScalarAsync(
                connection,
                "SELECT count(*) FROM sqlite_master;",
                cancellationToken).ConfigureAwait(false);

            return false;

        }
        catch (SqliteException)
        {

            return true;

        }

    }

    /// <summary>
    /// Exercises the FTS5 configuration Covenant's accelerator tier depends on: an external-content
    /// index with secure-delete enabled, whose rank-1 integrity check still passes after a delete
    /// and whose deleted token is genuinely gone.
    /// </summary>
    private static async Task<bool> FtsSecureDeletePassesAsync(
        string path,
        string key,
        CancellationToken cancellationToken)
    {

        await using SqliteConnection connection = Open(path, key);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "PRAGMA secure_delete=ON;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "CREATE TABLE fts_source (id INTEGER PRIMARY KEY, body TEXT NOT NULL);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "CREATE VIRTUAL TABLE fts_probe USING fts5(body, content='fts_source', content_rowid='id');",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "INSERT INTO fts_source (id, body) VALUES (1, 'keepsake'), (2, 'vanishing');",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "INSERT INTO fts_probe (fts_probe) VALUES ('rebuild');",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "INSERT INTO fts_probe (fts_probe, rank) VALUES ('secure-delete', 1);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "INSERT INTO fts_probe (fts_probe, rowid, body) VALUES ('delete', 2, 'vanishing');",
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "DELETE FROM fts_source WHERE id = 2;", cancellationToken)
            .ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "INSERT INTO fts_probe (fts_probe, rank) VALUES ('integrity-check', 1);",
            cancellationToken).ConfigureAwait(false);

        string? remaining = await ScalarAsync(
            connection,
            "SELECT count(*) FROM fts_probe WHERE fts_probe MATCH 'vanishing';",
            cancellationToken).ConfigureAwait(false);

        string? kept = await ScalarAsync(
            connection,
            "SELECT count(*) FROM fts_probe WHERE fts_probe MATCH 'keepsake';",
            cancellationToken).ConfigureAwait(false);

        return string.Equals(remaining, "0", StringComparison.Ordinal)
            && string.Equals(kept, "1", StringComparison.Ordinal);

    }

    /// <summary>
    /// The library is compiled with <c>SQLITE_OMIT_LOAD_EXTENSION</c>, so the SQL function does not
    /// exist at all. Either outcome — missing function or refused authorization — is a failure to
    /// load, which is what must be true.
    /// </summary>
    private static async Task<bool> LoadExtensionIsBlockedAsync(
        string path,
        string key,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteConnection connection = Open(path, key);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            _ = await ScalarAsync(
                connection,
                "SELECT load_extension('forbidden');",
                cancellationToken).ConfigureAwait(false);

            return false;

        }
        catch (SqliteException)
        {

            return true;

        }

    }

    private static async Task<List<string>> MissingCompileOptionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        HashSet<string> reported = new(StringComparer.Ordinal);

        await using (SqliteCommand command = connection.CreateCommand())
        {

            command.CommandText = "PRAGMA compile_options;";

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                _ = reported.Add(reader.GetString(0));

            }

        }

        return [.. RequiredPragmaCompileOptions.Where(option => !reported.Contains(option))];

    }

    /// <summary>
    /// Pooling is disabled so every open in this validation is a real open against the file, which
    /// is the only way a reopen proves anything about the codec.
    /// </summary>
    private static SqliteConnection Open(string path, string key) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,

            Password = key,

            Pooling = false,
        }.ToString());

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<string?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Convert.ToString(value);

    }

}
