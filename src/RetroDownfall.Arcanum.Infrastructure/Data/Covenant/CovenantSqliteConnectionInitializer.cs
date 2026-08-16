using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The single place SQLCipher and SQLite policy is applied to a connection.
/// </summary>
/// <remarks>
/// Applies policy and then reads it back. A pragma that silently does not take effect — a
/// journal-mode change refused because another connection holds the database, <c>foreign_keys</c>
/// ignored inside a transaction — leaves the process believing it has guarantees it does not have,
/// so the read-back is the point rather than a formality.
/// </remarks>
internal sealed class CovenantSqliteConnectionInitializer : ICovenantSqliteConnectionInitializer
{

    internal const int BusyTimeoutMs = 5000;

    /// <summary>
    /// SQL identifiers for the authorization functions. Fixed internal names, never input: they are
    /// interpolated into DDL-free scalar-function registration and read back by name below.
    /// </summary>
    private static readonly string[] FunctionNames =
    [
        "arcanum_session_binding_write_authorized",

        "arcanum_session_retention_authorized",

        "arcanum_owner_cleanup_authorized",

        "arcanum_artifact_replacement_authorized",

        "arcanum_sensitivity_purge_authorized",

        "arcanum_covenant_family_maintenance_authorized",

        "arcanum_accelerator_sync_authorized",

        "arcanum_protected_session_transfer_authorized",

        "arcanum_turn_capacity_mutation_authorized",

        "arcanum_campaign_path_marker_intent_mutation_authorized",

        "arcanum_managed_file_intent_mutation_authorized",

        "arcanum_restore_staging_managed_authority_sanitization_authorized",
    ];

    private static readonly ConditionalWeakTable<SqliteConnection, CovenantSqliteConnectionState> States = new();

    private readonly ISqliteNativeRuntime _nativeRuntime;

    internal CovenantSqliteConnectionInitializer(ISqliteNativeRuntime nativeRuntime) =>
        _nativeRuntime = nativeRuntime;

    /// <summary>
    /// For the static compatibility facade and design-time entry points that cannot take a
    /// dependency.
    /// </summary>
    internal static CovenantSqliteConnectionInitializer Instance { get; } =
        new(SqliteNativeRuntime.Instance);

    internal static string FunctionName(CovenantSqliteAuthorizationKind kind) =>
        FunctionNames[(int)kind];

    public async ValueTask InitializeAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        _nativeRuntime.Initialize();

        if (connection.State != ConnectionState.Open)
        {

            throw new InvalidOperationException(
                "A SQLCipher connection must be open before Covenant policy is applied to it.");

        }

        CovenantSqliteConnectionState state = States.GetOrCreateValue(connection);

        // A pooled connection handed back out must never inherit an authorization from its previous
        // logical use.
        state.Reset();

        RegisterAuthorizationFunctions(connection, state);

        await ApplyPolicyAsync(connection, mode, cancellationToken).ConfigureAwait(false);

        await VerifyPolicyAsync(connection, mode, cancellationToken).ConfigureAwait(false);

    }

    public CovenantSqliteAuthorizationScope Authorize(
        SqliteConnection connection,
        CovenantSqliteAuthorizationKind kind)
    {

        ArgumentNullException.ThrowIfNull(connection);

        if (kind == CovenantSqliteAuthorizationKind.RestoreStagingManagedAuthoritySanitization)
        {

            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Restore-staging authority sanitization is granted only through the sealed "
                + "restore-staging capability, never through the general authorization entry point.");

        }

        return AuthorizeCore(connection, kind);

    }

    /// <summary>
    /// Shared by the public entry point and the restore-staging capability, which is the only
    /// caller permitted to request
    /// <see cref="CovenantSqliteAuthorizationKind.RestoreStagingManagedAuthoritySanitization" />.
    /// </summary>
    internal CovenantSqliteAuthorizationScope AuthorizeCore(
        SqliteConnection connection,
        CovenantSqliteAuthorizationKind kind)
    {

        if (connection.State != ConnectionState.Open)
        {

            throw new InvalidOperationException(
                "A closed connection cannot be authorized for a privileged Grimoire operation.");

        }

        if (!States.TryGetValue(connection, out CovenantSqliteConnectionState? state))
        {

            throw new InvalidOperationException(
                "This connection was not initialized through the Covenant connection initializer, "
                + "so its authorization functions are not registered.");

        }

        state.Acquire(kind);

        return new CovenantSqliteAuthorizationScope(state, connection, kind);

    }

    /// <summary>
    /// Registers the authorization functions without touching pragmas or existing authorizations.
    /// </summary>
    /// <remarks>
    /// The schema's guard triggers call these functions, and SQLite resolves a function at
    /// <em>prepare</em> time — so a statement that merely touches a guarded table fails with "no
    /// such function" on a connection that never registered them, whether or not the guard would
    /// have allowed the write. Schema installation therefore has to guarantee the functions exist
    /// before it runs a single statement, independently of whether the caller applied connection
    /// policy first.
    ///
    /// <para>Deliberately no <c>Reset()</c>: this is called on connections that may already hold a
    /// live authorization scope, and dropping it here would revoke a permission mid-operation.</para>
    /// </remarks>
    internal void EnsureAuthorizationFunctions(SqliteConnection connection)
    {

        ArgumentNullException.ThrowIfNull(connection);

        _nativeRuntime.Initialize();

        RegisterAuthorizationFunctions(connection, States.GetOrCreateValue(connection));

    }

    /// <summary>
    /// Registers every authorization function against this connection's own state. Schema triggers
    /// call these to decide whether a privileged write is permitted; with no scope open they all
    /// return 0.
    /// </summary>
    private static void RegisterAuthorizationFunctions(
        SqliteConnection connection,
        CovenantSqliteConnectionState state)
    {

        foreach (CovenantSqliteAuthorizationKind kind in Enum.GetValues<CovenantSqliteAuthorizationKind>())
        {

            CovenantSqliteAuthorizationKind captured = kind;

            connection.CreateFunction(
                FunctionNames[(int)kind],
                () => state.IsAuthorized(captured) ? 1L : 0L);

        }

    }

    private static async Task ApplyPolicyAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        // secure_delete overwrites freed pages instead of leaving them readable in the file. It
        // matters for every tier, not just Covenant: a deleted row that survives in free space is
        // still recoverable from an encrypted database once the key is known.
        await ExecuteAsync(
            connection,
            $"""
             PRAGMA busy_timeout={BusyTimeoutMs};
             PRAGMA foreign_keys=ON;
             PRAGMA secure_delete=ON;
             """,
            cancellationToken).ConfigureAwait(false);

        if (mode is CovenantSqliteConnectionMode.ReadWrite)
        {

            await ExecuteAsync(
                connection,
                """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                """,
                cancellationToken).ConfigureAwait(false);

        }

        if (mode is CovenantSqliteConnectionMode.ExclusiveMaintenance)
        {

            await ExecuteAsync(
                connection,
                "PRAGMA locking_mode=EXCLUSIVE;",
                cancellationToken).ConfigureAwait(false);

        }

    }

    /// <summary>
    /// Reads back the policy that must have taken effect and fails closed when it did not.
    /// </summary>
    private static async Task VerifyPolicyAsync(
        SqliteConnection connection,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {

        await RequireAsync(connection, "PRAGMA foreign_keys;", "1", "foreign_keys", cancellationToken)
            .ConfigureAwait(false);

        await RequireAsync(connection, "PRAGMA secure_delete;", "1", "secure_delete", cancellationToken)
            .ConfigureAwait(false);

        await RequireAsync(
            connection,
            "PRAGMA busy_timeout;",
            BusyTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "busy_timeout",
            cancellationToken).ConfigureAwait(false);

        string? cipherVersion = await ScalarAsync(connection, "PRAGMA cipher_version;", cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(cipherVersion))
        {

            throw new InvalidOperationException(
                "This connection reports no cipher version, so it is not a SQLCipher connection. "
                + "Arcanum refuses to use an unencrypted SQLite engine for the Grimoire.");

        }

        if (mode is CovenantSqliteConnectionMode.ReadWrite)
        {

            string? journalMode = await ScalarAsync(connection, "PRAGMA journal_mode;", cancellationToken)
                .ConfigureAwait(false);

            // An in-memory database cannot use WAL; it reports "memory" and that is correct rather
            // than a policy failure.
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(journalMode, "memory", StringComparison.OrdinalIgnoreCase))
            {

                throw new InvalidOperationException(
                    $"Expected WAL journaling on a read-write Grimoire connection, but it reports '{journalMode}'.");

            }

        }

    }

    private static async Task RequireAsync(
        SqliteConnection connection,
        string sql,
        string expected,
        string label,
        CancellationToken cancellationToken)
    {

        string? actual = await ScalarAsync(connection, sql, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {

            throw new InvalidOperationException(
                $"Grimoire connection policy did not take effect: {label} reports '{actual}', expected '{expected}'.");

        }

    }

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
