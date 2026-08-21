using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Erases the canonical Covenant family in one exclusive initialized secure-delete transaction, and
/// reads back the generation of the single dataset that transaction created.
/// </summary>
/// <remarks>
/// The first half of the <c>ICovenantErasureTransition</c> seam: everything up to and including
/// <c>CanonicalResetApplied</c>. It performs no file-level scrub and publishes nothing, because
/// neither is true yet at the moment this transaction commits (§10.20.5).
/// </remarks>
internal interface ICovenantCanonicalErasure
{

    /// <summary>
    /// Removes the family's rows and stamps one new dataset, reporting its generation.
    /// </summary>
    /// <param name="operation">
    /// <see cref="CovenantExclusiveOperation.CovenantReset"/> or
    /// <see cref="CovenantExclusiveOperation.HealthyCatalogFactoryErasure"/>. The code selects what is
    /// preserved rather than what is deleted.
    /// </param>
    Task<Result<Guid>> ApplyAsync(
        CovenantExclusiveOperation operation,
        CancellationToken cancellationToken);

}

/// <summary>
/// The one transaction that removes the Covenant family's rows, and the exact evidence it leaves.
/// </summary>
/// <remarks>
/// One connection and one transaction, both by construction. The family is a graph — provenance
/// under versions, heads over versions and entries, an outbox and a projection keyed by identities
/// the same transaction is deleting — and a second connection or a second transaction would make a
/// crash between two of those deletions a state no reader has a name for.
///
/// <para>The two arms do the same thing to storage. A healthy-catalog factory erasure keeps schema
/// objects, <c>grimoire_feature_schemas</c>, authority taint, and nonrevocable disclosure evidence
/// that an ordinary reset also keeps, and differs only in reseeding the canonical and accelerator
/// singletons: it can run against a catalog whose optional tier was reinstalled underneath it, where
/// a reset — which resets a family it is entitled to assume exists — must refuse instead.</para>
///
/// <para>Everything preserved is preserved by not being named. The canonical tables are listed
/// literally rather than matched on a <c>covenant_</c> prefix, because core owns two tables that
/// share it: <c>covenant_authority_state</c> carries the host-tools taint an erasure must never
/// clear, and <c>covenant_schema_repair_intents</c> is the journal a damaged catalog is repaired
/// from. A prefix would take both (§10.12).</para>
/// </remarks>
internal sealed class CovenantCanonicalErasureTransaction : ICovenantCanonicalErasure
{

    /// <summary>The Covenant family's row in the shared per-capability cleanup cursor.</summary>
    private const int CovenantFamilyCode = (int)GrimoireSchemaFamily.Covenant;

    private const long CampaignOwnerKindCode = 1;

    private const long SessionOwnerKindCode = 2;

    /// <summary>
    /// Every canonical table the erasure empties, children before parents.
    /// </summary>
    /// <remarks>
    /// The order is a foreign-key order, not a preference. Provenance references versions, heads
    /// reference versions and entries by a composite key, and versions reference entries and
    /// themselves — so a different order trips a constraint the canonical tier deliberately relies on.
    /// The two receipt tables and the outbox come first only because nothing references them and
    /// putting them last would read as if something did.
    /// </remarks>
    private static readonly string[] FamilyTablesInDeletionOrder =
    [
        "covenant_turn_receipts",
        "covenant_turn_receipt_aggregate",
        "covenant_mutation_receipts",
        "covenant_search_outbox",
        "covenant_version_attachment_provenance",
        "covenant_heads",
        "covenant_versions",
        "covenant_entries",
        "covenant_key_epochs",
    ];

    /// <summary>
    /// The same canonical tables, for the proof that re-reads them once the erasure claims they are
    /// empty.
    /// </summary>
    /// <remarks>
    /// One list rather than two. A second copy could only ever differ in the case that matters: a
    /// table this transaction stopped naming would also be a table the storage-health proof stopped
    /// counting, and the erasure would report a family it had not finished emptying.
    /// </remarks>
    internal static IReadOnlyList<string> FamilyTables { get; } =
        Array.AsReadOnly(FamilyTablesInDeletionOrder);

    private readonly ICovenantMaintenanceConnectionFactory _connections;

    private readonly ICovenantSqliteConnectionInitializer _initializer;

    private readonly ICovenantConnectionDrain _drain;

    private readonly TimeProvider _timeProvider;

    internal CovenantCanonicalErasureTransaction(
        ICovenantMaintenanceConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        ICovenantConnectionDrain drain,
        TimeProvider timeProvider)
    {

        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

        _drain = drain ?? throw new ArgumentNullException(nameof(drain));

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    }

    public async Task<Result<Guid>> ApplyAsync(
        CovenantExclusiveOperation operation,
        CancellationToken cancellationToken)
    {

        if (operation is not CovenantExclusiveOperation.CovenantReset
            and not CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
        {

            return Result<Guid>.Failure(
                new Error(
                    ErrorCodes.Covenant.InvalidScope,
                    "Only a Covenant reset or a healthy-catalog factory erasure erases the canonical family."));

        }

        // Before the connection, never after. An exclusive maintenance handle cannot take its lock
        // while any other handle holds the same database open, so draining afterwards would mean
        // failing on a lock whose holder this component had just declined to close.
        Result drained = await _drain.DrainAsync(cancellationToken).ConfigureAwait(false);

        if (drained.IsFailure)
        {

            return Result<Guid>.Failure(drained.Error);

        }

        SqliteConnection connection;

        try
        {

            connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException failed)
        {

            return Failure(failed, "open an exclusive maintenance connection");

        }

        await using (connection.ConfigureAwait(false))
        {

            return await ApplyOnConnectionAsync(connection, operation, cancellationToken).ConfigureAwait(false);

        }

    }

    private async Task<Result<Guid>> ApplyOnConnectionAsync(
        SqliteConnection connection,
        CovenantExclusiveOperation operation,
        CancellationToken cancellationToken)
    {

        try
        {

            await _initializer
                .InitializeAsync(connection, CovenantSqliteConnectionMode.ExclusiveMaintenance, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (InvalidOperationException failed)
        {

            return Result<Guid>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    $"A Covenant erasure connection did not initialize: {failed.Message}"));

        }
        catch (SqliteException failed)
        {

            return Failure(failed, "initialize an exclusive maintenance connection");

        }

        try
        {

            Result proven = await RequireSecureDeleteAsync(connection, cancellationToken).ConfigureAwait(false);

            if (proven.IsFailure)
            {

                return Result<Guid>.Failure(proven.Error);

            }

            using CovenantSqliteAuthorizationScope authorization = _initializer.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Result<Guid> erased = await EraseAsync(connection, transaction, operation, cancellationToken)
                .ConfigureAwait(false);

            if (erased.IsFailure)
            {

                return erased;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return erased;

        }
        catch (SqliteException failed)
        {

            return Failure(failed, "erase the Covenant canonical family");

        }

    }

    /// <summary>
    /// Everything one erasure removes and everything it stamps, inside the caller's one transaction.
    /// </summary>
    private async Task<Result<Guid>> EraseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantExclusiveOperation operation,
        CancellationToken cancellationToken)
    {

        bool factoryErasure = operation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure;

        bool acceleratorInstalled = await ObjectExistsAsync(
            connection,
            transaction,
            "covenant_search_documents",
            cancellationToken).ConfigureAwait(false);

        // The projection carries the allocated search identities, so it goes with the dataset that
        // allocated them. Its after-delete trigger subtracts the tokens from the external-content
        // index, which a plain delete against the index itself would corrupt rather than clear.
        if (acceleratorInstalled)
        {

            _ = await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM covenant_search_documents;",
                cancellationToken).ConfigureAwait(false);

        }

        foreach (string table in FamilyTablesInDeletionOrder)
        {

            _ = await ExecuteAsync(
                connection,
                transaction,
                $"DELETE FROM \"{table}\";",
                cancellationToken).ConfigureAwait(false);

        }

        long campaignSequence = await ReadOwnerSequenceAsync(
            connection,
            transaction,
            CampaignOwnerKindCode,
            cancellationToken).ConfigureAwait(false);

        long sessionSequence = await ReadOwnerSequenceAsync(
            connection,
            transaction,
            SessionOwnerKindCode,
            cancellationToken).ConfigureAwait(false);

        string updatedAtUtc = CovenantCanonicalSchemaDataInitializer.FormatTimestamp(
            _timeProvider.GetUtcNow());

        Result<Guid> generation = await StampDatasetAsync(
            connection,
            transaction,
            factoryErasure,
            campaignSequence,
            sessionSequence,
            updatedAtUtc,
            cancellationToken).ConfigureAwait(false);

        if (generation.IsFailure)
        {

            return generation;

        }

        await ResetCleanupCursorAsync(
            connection,
            transaction,
            campaignSequence,
            sessionSequence,
            updatedAtUtc,
            cancellationToken).ConfigureAwait(false);

        if (factoryErasure && acceleratorInstalled)
        {

            Result reseeded = await ReseedAcceleratorAsync(connection, transaction, cancellationToken)
                .ConfigureAwait(false);

            if (reseeded.IsFailure)
            {

                return Result<Guid>.Failure(reseeded.Error);

            }

        }

        return generation;

    }

    /// <summary>
    /// Stamps the single new dataset and restarts everything that belongs to a generation.
    /// </summary>
    /// <remarks>
    /// The two search counters restart and the three epochs advance, which is the singleton's own
    /// rule rather than this method's preference: a reset stamps a new generation and discards every
    /// projection built under the old one, so the counters may restart exactly then — while a turn
    /// that captured an epoch before the reset must not find that value still valid against the
    /// dataset that replaced it.
    /// </remarks>
    private static async Task<Result<Guid>> StampDatasetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        bool factoryErasure,
        long campaignSequence,
        long sessionSequence,
        string updatedAtUtc,
        CancellationToken cancellationToken)
    {

        // Cryptographic random rather than a sequential identity, matching the canonical installer:
        // this value is compared by equality across processes and snapshots, and a generation that
        // could be predicted from the last one would let a stale reader guess its way past a check.
        Guid generation = new(RandomNumberGenerator.GetBytes(16));

        int updated = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE covenant_state
            SET DatasetGeneration = $generation,
                CanonicalSearchSequence = 0,
                AppliedDatasetGeneration = NULL,
                AppliedSearchSequence = NULL,
                AppliedCampaignDeletionSequence = $campaignSequence,
                AppliedSessionDeletionSequence = $sessionSequence,
                AcceleratorEpoch = AcceleratorEpoch + 1,
                KeyReclamationEpoch = KeyReclamationEpoch + 1,
                EnvelopeKeyEpoch = EnvelopeKeyEpoch + 1,
                NextSearchRowId = 1,
                RebuildStateCode = $rebuildStateCode,
                RebuildTargetSequence = NULL,
                RebuildCursor = NULL,
                UpdatedAtUtc = $updatedAtUtc
            WHERE StateKey = 1;
            """,
            cancellationToken,
            ("$generation", generation.ToByteArray()),
            ("$campaignSequence", campaignSequence),
            ("$sessionSequence", sessionSequence),
            ("$rebuildStateCode", (long)CovenantFtsRebuildState.FullRebuildRequired),
            ("$updatedAtUtc", updatedAtUtc)).ConfigureAwait(false);

        if (updated == 1)
        {

            return Result<Guid>.Success(generation);

        }

        if (!factoryErasure)
        {

            // A reset reseeds nothing. Minting a singleton for a catalog that lost one would answer
            // schema damage by inventing a dataset identity nothing else in the installation agrees
            // with, and the operator would never be told the catalog was damaged at all.
            return Result<Guid>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The Covenant canonical singleton is absent, so a reset has no dataset to replace."));

        }

        _ = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO covenant_state (
                StateKey,
                DatasetGeneration,
                CanonicalSearchSequence,
                AppliedDatasetGeneration,
                AppliedSearchSequence,
                AppliedCampaignDeletionSequence,
                AppliedSessionDeletionSequence,
                AcceleratorEpoch,
                KeyReclamationEpoch,
                EnvelopeMasterKeyVersion,
                EnvelopeMasterKeyFingerprint,
                EnvelopeKeyEpoch,
                NextSearchRowId,
                RebuildStateCode,
                RebuildTargetSequence,
                RebuildCursor,
                UpdatedAtUtc)
            SELECT
                1,
                $generation,
                0,
                NULL,
                NULL,
                $campaignSequence,
                $sessionSequence,
                1,
                1,
                CurrentMasterKeyVersion,
                CurrentMasterKeyFingerprint,
                1,
                1,
                $rebuildStateCode,
                NULL,
                NULL,
                $updatedAtUtc
            FROM covenant_authority_state
            WHERE StateKey = 1;
            """,
            cancellationToken,
            ("$generation", generation.ToByteArray()),
            ("$campaignSequence", campaignSequence),
            ("$sessionSequence", sessionSequence),
            ("$rebuildStateCode", (long)CovenantFtsRebuildState.FullRebuildRequired),
            ("$updatedAtUtc", updatedAtUtc)).ConfigureAwait(false);

        // The envelope columns are copied from the core authority row rather than invented, so a
        // reseeded singleton describes the key this installation actually holds. No row there means
        // the insert selected nothing: there is no installation identity to reseed against, and
        // guessing one would be worse than refusing.
        return await CountAsync(connection, transaction, "covenant_state", cancellationToken)
                .ConfigureAwait(false) == 1
            ? Result<Guid>.Success(generation)
            : Result<Guid>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The Covenant canonical singleton could not be reseeded from the core authority row."));

    }

    /// <summary>
    /// Moves the shared per-capability cleanup cursor up to the journal it has just been excused from.
    /// </summary>
    /// <remarks>
    /// The cursor is set to the journal's current maximum rather than to zero, exactly as a first
    /// installation seeds it. Zero would claim the family had applied nothing while the journal
    /// already held events, and the next sweep would replay deletions against a dataset with no rows
    /// to delete. The sweep flag clears for the same reason: a dataset with no rows owes none.
    ///
    /// <para>Both arms write it, including the arm that refuses to reseed a missing canonical
    /// singleton. This row is core bookkeeping rather than an identity, and the value it should hold
    /// was computed from the journal a moment ago — so creating an absent one invents nothing, while
    /// leaving it absent would hand the sweep no watermark at all.</para>
    /// </remarks>
    private static async Task ResetCleanupCursorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long campaignSequence,
        long sessionSequence,
        string updatedAtUtc,
        CancellationToken cancellationToken)
    {

        int updated = await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE capability_cleanup_state
            SET AppliedCampaignSequence = $campaignSequence,
                AppliedSessionSequence = $sessionSequence,
                FullSweepRequired = 0,
                UpdatedAtUtc = $updatedAtUtc
            WHERE CapabilityFamilyCode = $family;
            """,
            cancellationToken,
            ("$campaignSequence", campaignSequence),
            ("$sessionSequence", sessionSequence),
            ("$updatedAtUtc", updatedAtUtc),
            ("$family", (long)CovenantFamilyCode)).ConfigureAwait(false);

        if (updated == 1)
        {

            return;

        }

        _ = await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO capability_cleanup_state (
                CapabilityFamilyCode, AppliedCampaignSequence, AppliedSessionSequence, FullSweepRequired, UpdatedAtUtc)
            VALUES ($family, $campaignSequence, $sessionSequence, 0, $updatedAtUtc)
            ON CONFLICT (CapabilityFamilyCode) DO NOTHING;
            """,
            cancellationToken,
            ("$family", (long)CovenantFamilyCode),
            ("$campaignSequence", campaignSequence),
            ("$sessionSequence", sessionSequence),
            ("$updatedAtUtc", updatedAtUtc)).ConfigureAwait(false);

    }

    /// <summary>
    /// Re-asserts the accelerator's own singleton configuration and proves it took.
    /// </summary>
    /// <remarks>
    /// FTS5 secure delete is a property of the index rather than of the database, and it is the one
    /// Covenant object holding plaintext-derived tokens in its own pages. A factory erasure that left
    /// it off would leave retired words legible in freed pages of an index that reads as empty.
    /// </remarks>
    private static async Task<Result> ReseedAcceleratorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        _ = await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('secure-delete', 1);",
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT v FROM covenant_fts_config WHERE k = 'secure-delete';";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not null and not DBNull
            && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The Covenant accelerator did not report secure delete after it was reseeded.");

    }

    /// <summary>
    /// Proves this connection overwrites freed pages, before a single row is deleted through it.
    /// </summary>
    /// <remarks>
    /// A second read-back rather than trust in the initializer's. The initializer proves its own
    /// policy and throws when it cannot, which is right for it and not the same statement as "the
    /// connection this deletion runs on has secure delete on": the promise an erasure makes is that
    /// the bytes are unrecoverable, and it is the deletion, not the initialization, that has to be
    /// able to keep it.
    /// </remarks>
    private static async Task<Result> RequireSecureDeleteAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA secure_delete;";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not null and not DBNull
            && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A Covenant erasure connection cannot prove secure delete, so it deletes nothing.");

    }

    private static async Task<long> ReadOwnerSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ownerKindCode,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT COALESCE(MAX(Sequence), 0)
            FROM owner_deletion_events
            WHERE OwnerKindCode = $ownerKindCode;
            """;

        _ = command.Parameters.AddWithValue("$ownerKindCode", ownerKindCode);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task<bool> ObjectExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT 1 FROM sqlite_master WHERE \"name\" = $name LIMIT 1;";

        _ = command.Parameters.AddWithValue("$name", name);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not null and not DBNull;

    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static Result<Guid> Failure(SqliteException failed, string step) =>
        Result<Guid>.Failure(
            new Error(
                ErrorCodes.Covenant.MaintenanceFailed,
                $"A Covenant erasure could not {step}: {failed.Message}"));

}
