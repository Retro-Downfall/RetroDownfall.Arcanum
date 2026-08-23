using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// One durable Campaign path marker intent, as the lifecycle reads it back.
/// </summary>
/// <remarks>
/// Content-free apart from the display path, which is diagnostics only. Nothing here can be turned
/// into a filesystem capability: the marker bytes are never stored for a cleanup intent, and the
/// digest that is stored is a one-way commitment to what preparation observed.
/// </remarks>
internal sealed record CampaignPathMarkerIntentRow(
    Guid IntentId,
    Guid OwnerOperationId,
    Guid CampaignId,
    CampaignPathMarkerIntentKind Kind,
    CovenantExclusiveOperation? ExclusiveOwnerOperation,
    CovenantDigest OwnerEffectDigest,
    CovenantDigest MarkerDigest,
    // Null only for a full installation reset cleanup child whose Campaign was already unavailable
    // or mismatched when the reset observed it. Every other kind is refused a null by the table.
    string? TargetDisplayPath,
    long PriorRevision,
    CampaignPathMarkerPhase Phase,
    long PhaseRevision,
    CovenantExclusiveLeaseDisposition? PendingDisposition);

/// <summary>
/// The only reader and writer of <c>campaign_path_marker_intents</c>.
/// </summary>
/// <remarks>
/// Transaction-bound and never registered in DI. Every statement borrows the connection-local marker
/// intent mutation scope, which begins <see langword="false"/> on every connection including a pooled
/// one — so a caller that reached this type without a live caller-owned transaction still writes
/// nothing (§10.12).
///
/// <para>Deliberately not a general repository. Whoever can insert one of these rows can make startup
/// recovery act on a workspace path with the installation's own authority, so the surface is exactly
/// the four operations the two-phase protocol needs and no query builder at all.</para>
/// </remarks>
internal sealed class CampaignPathMarkerIntentStore
{

    private readonly CovenantSqliteConnectionInitializer _initializer;

    private readonly SqliteConnection _connection;

    private readonly SqliteTransaction _transaction;

    private readonly TimeProvider _timeProvider;

    internal CampaignPathMarkerIntentStore(
        CovenantSqliteConnectionInitializer initializer,
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeProvider timeProvider)
    {

        ArgumentNullException.ThrowIfNull(initializer);

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(timeProvider);

        // The transaction has to belong to this connection. A store that accepted a foreign
        // transaction would authorize on one connection and write through another, and the guard
        // trigger would see an unauthorized connection while the caller believed it had permission.
        if (!ReferenceEquals(transaction.Connection, connection))
        {

            throw new ArgumentException(
                "A Campaign path marker intent store requires the live transaction of its own connection.",
                nameof(transaction));

        }

        _initializer = initializer;

        _connection = connection;

        _transaction = transaction;

        _timeProvider = timeProvider;

    }

    /// <summary>
    /// Commits one <see cref="CampaignPathMarkerIntentKind.RestoreCleanup"/> child, or returns the
    /// identity an earlier attempt already committed for the same owner and Campaign.
    /// </summary>
    /// <remarks>
    /// Replay-safe by the durable owner/Campaign/kind uniqueness rather than by a caller-held flag:
    /// a retry that crashed between the insert and its own bookkeeping has to reach the row it
    /// already wrote, or the second attempt would journal a child the first one's checkpoint does not
    /// name.
    /// </remarks>
    internal async Task<Result<Guid>> InsertOrReadRestoreCleanupAsync(
        Guid ownerOperationId,
        CovenantDigest ownerEffectDigest,
        Guid campaignId,
        CovenantDigest markerDigest,
        string targetDisplayPath,
        long priorRevision,
        CancellationToken cancellationToken)
    {

        if (ownerOperationId == Guid.Empty
            || campaignId == Guid.Empty
            || !ownerEffectDigest.IsValid
            || !markerDigest.IsValid
            || string.IsNullOrWhiteSpace(targetDisplayPath)
            || targetDisplayPath.Length > 4096
            || priorRevision < 0)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore cleanup intent requires a complete owner, Campaign, evidence, and target.");

        }

        Guid? existing = await ReadExistingIntentIdAsync(
            ownerOperationId,
            campaignId,
            CampaignPathMarkerIntentKind.RestoreCleanup,
            cancellationToken).ConfigureAwait(false);

        if (existing is { } replayed)
        {

            return replayed;

        }

        Guid intentId = Guid.NewGuid();

        string now = Iso(_timeProvider.GetUtcNow());

        using CovenantSqliteAuthorizationScope scope = _initializer.Authorize(
            _connection,
            CovenantSqliteAuthorizationKind.CampaignPathMarkerIntentMutation);

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = """
            INSERT INTO campaign_path_marker_intents (
                IntentId, OwnerOperationId, CampaignId, IntentKindCode, ExclusiveOwnerOperationCode,
                OwnerEffectDigest, EncryptedMarkerPayload, MarkerDigest, ApplyRequestDigest,
                TemporaryBaseName, TemporaryPhysicalIdentityDigest, TargetDisplayPath, PriorRevision,
                TargetObservationCode, ReopenedTargetPhysicalIdentityDigest, PendingDispositionCode,
                PhaseCode, PhaseRevision, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                $intent, $owner, $campaign, 3, 5,
                $effect, NULL, $marker, NULL,
                NULL, NULL, $display, $revision,
                NULL, NULL, NULL,
                1, 1, $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$intent", intentId.ToString("D"));

        _ = command.Parameters.AddWithValue("$owner", ownerOperationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$campaign", campaignId.ToString("D"));

        _ = command.Parameters.AddWithValue("$effect", ownerEffectDigest.Bytes);

        _ = command.Parameters.AddWithValue("$marker", markerDigest.Bytes);

        _ = command.Parameters.AddWithValue("$display", targetDisplayPath);

        _ = command.Parameters.AddWithValue("$revision", priorRevision);

        _ = command.Parameters.AddWithValue("$now", now);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return intentId;

    }

    /// <summary>
    /// Reads one child by identity, or reports that it is absent.
    /// </summary>
    internal async Task<CampaignPathMarkerIntentRow?> ReadAsync(
        Guid intentId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = """
            SELECT IntentId, OwnerOperationId, CampaignId, IntentKindCode, ExclusiveOwnerOperationCode,
                   OwnerEffectDigest, MarkerDigest, TargetDisplayPath, PriorRevision, PhaseCode,
                   PhaseRevision, PendingDispositionCode
            FROM campaign_path_marker_intents
            WHERE IntentId = $intent;
            """;

        _ = command.Parameters.AddWithValue("$intent", intentId.ToString("D"));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        return new CampaignPathMarkerIntentRow(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            (CampaignPathMarkerIntentKind)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : (CovenantExclusiveOperation)reader.GetInt32(4),
            new CovenantDigest(ReadBlob(reader, 5)),
            new CovenantDigest(ReadBlob(reader, 6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt64(8),
            (CampaignPathMarkerPhase)reader.GetInt32(9),
            reader.GetInt64(10),
            reader.IsDBNull(11) ? null : (CovenantExclusiveLeaseDisposition)reader.GetInt32(11));

    }

    /// <summary>
    /// Counts the children one owner holds, so an extra row beneath a proven checkpoint is visible.
    /// </summary>
    internal async Task<long> CountForOwnerAsync(
        Guid ownerOperationId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = """
            SELECT COUNT(*) FROM campaign_path_marker_intents
            WHERE OwnerOperationId = $owner AND IntentKindCode = 3;
            """;

        _ = command.Parameters.AddWithValue("$owner", ownerOperationId.ToString("D"));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// Advances one child by exactly one phase revision, or fails without changing anything.
    /// </summary>
    /// <remarks>
    /// The compare-and-swap is on the phase revision the caller last observed, so two recovery
    /// attempts racing the same child produce one winner and one typed conflict rather than two
    /// advances that each believe they moved it.
    /// </remarks>
    internal async Task<Result> AdvancePhaseAsync(
        Guid intentId,
        long expectedPhaseRevision,
        CampaignPathMarkerPhase nextPhase,
        CampaignPathMarkerTargetObservation? observation,
        CovenantExclusiveLeaseDisposition? pendingDisposition,
        CancellationToken cancellationToken)
    {

        string now = Iso(_timeProvider.GetUtcNow());

        using CovenantSqliteAuthorizationScope scope = _initializer.Authorize(
            _connection,
            CovenantSqliteAuthorizationKind.CampaignPathMarkerIntentMutation);

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = """
            UPDATE campaign_path_marker_intents
            SET PhaseCode = $phase,
                PhaseRevision = PhaseRevision + 1,
                TargetObservationCode = COALESCE($observation, TargetObservationCode),
                ReopenedTargetPhysicalIdentityDigest =
                    COALESCE($identity, ReopenedTargetPhysicalIdentityDigest),
                PendingDispositionCode = $disposition,
                UpdatedAtUtc = $now
            WHERE IntentId = $intent AND PhaseRevision = $expected;
            """;

        _ = command.Parameters.AddWithValue("$phase", (int)nextPhase);

        _ = command.Parameters.AddWithValue(
            "$observation",
            observation is { } present ? (int)present.Code : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$identity",
            observation?.PhysicalIdentityDigest is { IsValid: true } digest
                ? digest.Bytes
                : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$disposition",
            pendingDisposition is { } disposition ? (int)disposition : DBNull.Value);

        _ = command.Parameters.AddWithValue("$now", now);

        _ = command.Parameters.AddWithValue("$intent", intentId.ToString("D"));

        _ = command.Parameters.AddWithValue("$expected", expectedPhaseRevision);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return affected == 1
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "A Campaign path marker intent phase advance lost its compare-and-swap."));

    }

    private async Task<Guid?> ReadExistingIntentIdAsync(
        Guid ownerOperationId,
        Guid campaignId,
        CampaignPathMarkerIntentKind kind,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = _transaction;

        command.CommandText = """
            SELECT IntentId FROM campaign_path_marker_intents
            WHERE OwnerOperationId = $owner AND CampaignId = $campaign AND IntentKindCode = $kind;
            """;

        _ = command.Parameters.AddWithValue("$owner", ownerOperationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$campaign", campaignId.ToString("D"));

        _ = command.Parameters.AddWithValue("$kind", (int)kind);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Guid.Parse((string)value);

    }

    private static byte[] ReadBlob(SqliteDataReader reader, int ordinal)
    {

        using System.IO.Stream stream = reader.GetStream(ordinal);

        using System.IO.MemoryStream buffer = new();

        stream.CopyTo(buffer);

        return buffer.ToArray();

    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

}

/// <summary>
/// The one-time proof that a cleanup target was reopened, or proven absent, on a real handle.
/// </summary>
/// <remarks>
/// Written exactly once and never rewritten, which the schema enforces. Its value is entirely that it
/// came from a specific open handle at a specific moment, so a second write would let a fabricated
/// identity pass the same-handle comparison a later phase depends on.
/// </remarks>
internal readonly record struct CampaignPathMarkerTargetObservation(
    CampaignPathMarkerTargetObservationCode Code,
    CovenantDigest? PhysicalIdentityDigest);

/// <summary>
/// Whether the cleanup target was opened or proven absent, matching
/// <c>campaign_path_marker_intents.TargetObservationCode</c>.
/// </summary>
internal enum CampaignPathMarkerTargetObservationCode
{

    Opened = 1,

    Absent = 2,

}
