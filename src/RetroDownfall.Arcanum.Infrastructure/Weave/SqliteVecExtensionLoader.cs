using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Attempts to enable SQLite extension loading and load the sqlite-vec native library
/// into a connection, verifying success with <c>SELECT vec_version()</c>. Uses the same low-level
/// <c>SQLitePCL.raw</c> API that <c>GrimoireSqlSchemaMigrator</c> already uses for its
/// <c>sqlite3_exec</c> migration runner (see <c>GrimoireSqlSchemaMigrator.cs</c>), so this stays
/// consistent with how the codebase talks to the native SQLite handle.
///
/// Risk (documented, not a defect): <c>SQLitePCLRaw.bundle_e_sqlcipher</c> — the SQLCipher provider
/// this database uses — may have <c>sqlite3_enable_load_extension</c> compiled out for security. If it
/// returns non-OK (or anything here throws), <see cref="TryLoad"/> returns <c>false</c> and callers
/// fall back to the managed cosine path. This is never treated as a failure: the vec0 index is purely
/// an acceleration layer, and the managed brute-force cosine search over the BLOB source-of-truth
/// tables is the guaranteed baseline.
///
/// No sqlite-vec NuGet package is referenced by the solution (see
/// <c>docs/Arcanum.DESIGN.md</c> §21), so packaged hosts default to the managed path unless a compatible
/// native extension is otherwise available at <see cref="ExtensionLibraryName"/>.
/// </summary>
internal static class SqliteVecExtensionLoader
{

    // sqlite-vec's upstream loadable-extension name; the platform loader resolves the actual file
    // (vec0.dylib / vec0.so / vec0.dll) from the process's library search path. No such library ships
    // with Arcanum — see class remarks.
    private const string ExtensionLibraryName = "vec0";

    public static bool TryLoad(SqliteConnection connection, ILogger? logger = null)
    {

        try
        {

            if (connection.State != ConnectionState.Open)
            {
                return false;

            }

            sqlite3 db = connection.Handle
                ?? throw new InvalidOperationException("SQLite connection handle is not available.");

            int enableResult = raw.sqlite3_enable_load_extension(db, 1);

            if (enableResult != raw.SQLITE_OK)
            {

                logger?.LogInformation(
                    "sqlite3_enable_load_extension returned {Code}; The Weave will use the managed cosine fallback only.",
                    enableResult);

                return false;

            }

            int loadResult = raw.sqlite3_load_extension(
                db,
                utf8z.FromString(ExtensionLibraryName),
                default,
                out utf8z errMsg);

            if (loadResult != raw.SQLITE_OK)
            {

                string detail = errMsg.utf8_to_string();

                logger?.LogInformation(
                    "sqlite-vec extension unavailable ({Detail}); The Weave will use the managed cosine fallback only.",
                    string.IsNullOrEmpty(detail) ? "no native library found" : detail);

                return false;

            }

            object? version = ExecuteScalar(connection, "SELECT vec_version();");

            if (version is null or DBNull)
            {

                logger?.LogWarning("sqlite-vec loaded but vec_version() returned no result; treating as unavailable.");

                return false;

            }

            logger?.LogInformation(
                "sqlite-vec extension loaded (version {Version}); The Weave will use accelerated vec0 search.",
                version);

            return true;

        }
        catch (Exception ex)
        {

            logger?.LogDebug(ex, "sqlite-vec extension load failed; The Weave will use the managed cosine fallback only.");

            return false;

        }

    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {

        using SqliteCommand cmd = connection.CreateCommand();

        cmd.CommandText = sql;

        return cmd.ExecuteScalar();

    }

}
