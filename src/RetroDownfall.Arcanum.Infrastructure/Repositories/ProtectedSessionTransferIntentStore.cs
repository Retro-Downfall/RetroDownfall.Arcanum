using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// The durable phases of a protected transfer's parent journal, matching
/// <c>protected_session_transfer_intents.PhaseCode</c>.
/// </summary>
internal enum ProtectedSessionTransferPhase
{

    Prepared = 1,

    BlobsStaged = 2,

    DatabaseCommitted = 3,

    ReopenPending = 4,

    Completed = 5,

    Abandoned = 6,

}

/// <summary>
/// The durable phases of one staged attachment blob, matching
/// <c>protected_session_transfer_blobs.PhaseCode</c>.
/// </summary>
internal enum ProtectedSessionTransferBlobPhase
{

    Prepared = 1,

    TempCreated = 2,

    TempWritten = 3,

    TempFsynced = 4,

    RenamedNoReplace = 5,

    ParentFsynced = 6,

    ReopenedVerified = 7,

    Referenced = 8,

    Cleaned = 9,

}

/// <summary>
/// One protected transfer's durable owner, as recovery reads it back.
/// </summary>
internal sealed record ProtectedSessionTransferIntentRow(
    Guid OperationId,
    CovenantDigest EffectDigest,
    CovenantDigest SourceEvidenceDigest,
    CovenantDigest DestinationBindingDigest,
    CovenantScope DestinationScope,
    Guid? DestinationCampaignId,
    Guid DestinationSessionId,
    CovenantDigest AttachmentManifestDigest,
    long AttachmentManifestCount,
    ProtectedSessionTransferPhase Phase,
    CovenantExclusiveLeaseDisposition? PendingDisposition,
    long Revision);

/// <summary>
/// The only reader and writer of the protected-transfer journal and its blob children.
/// </summary>
/// <remarks>
/// Transaction-bound and never registered in DI. Every write borrows the connection-local protected
/// transfer authorization, which begins <see langword="false"/> on every connection, so a caller that
/// reached this type outside a live transfer still writes nothing.
/// </remarks>
internal sealed class ProtectedSessionTransferIntentStore
{

    private readonly CovenantSqliteConnectionInitializer _initializer;

    private readonly SqliteConnection _connection;

    private readonly TimeProvider _timeProvider;

    internal ProtectedSessionTransferIntentStore(
        CovenantSqliteConnectionInitializer initializer,
        SqliteConnection connection,
        TimeProvider timeProvider)
    {

        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    }

    /// <summary>
    /// Commits the parent owner before any filesystem byte, or reads back an exact replay.
    /// </summary>
    /// <remarks>
    /// A replay whose plan changed is a conflict rather than a second attempt. Recovery reconstructs
    /// what to resume from these fields alone, so an operation identity that could be reused with a
    /// different destination would let a resumed transfer commit into a Session nobody asked for.
    /// </remarks>
    internal async Task<Result<ProtectedSessionTransferIntentRow>> PrepareAsync(
        ProtectedSessionTransferIntentRow intent,
        byte[] destinationRootIdentityEvidence,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        ProtectedSessionTransferIntentRow? existing =
            await ReadAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {

            return existing.EffectDigest == intent.EffectDigest
                && existing.DestinationSessionId == intent.DestinationSessionId
                && existing.DestinationBindingDigest == intent.DestinationBindingDigest
                && existing.AttachmentManifestDigest == intent.AttachmentManifestDigest
                ? existing
                : Result<ProtectedSessionTransferIntentRow>.Failure(
                    new Error(
                        ErrorCodes.Security.IdempotencyConflict,
                        "This transfer operation identity already names a different destination or plan."));

        }

        string now = Iso(_timeProvider.GetUtcNow());

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO protected_session_transfer_intents (
                OperationId, EffectDigest, SourceEvidenceDigest, DestinationBindingDigest,
                DestinationScopeCode, DestinationCampaignId, DestinationSessionId,
                AttachmentManifestDigest, AttachmentManifestCount, DestinationRootIdentityEvidence,
                PhaseCode, PendingDispositionCode, Revision, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($operation, $effect, $sourceEvidence, $binding,
                    $scope, $campaign, $session,
                    $manifest, $count, $root,
                    1, NULL, 0, $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$effect", intent.EffectDigest.Bytes);

        _ = command.Parameters.AddWithValue("$sourceEvidence", intent.SourceEvidenceDigest.Bytes);

        _ = command.Parameters.AddWithValue("$binding", intent.DestinationBindingDigest.Bytes);

        _ = command.Parameters.AddWithValue("$scope", (int)intent.DestinationScope);

        _ = command.Parameters.AddWithValue(
            "$campaign",
            intent.DestinationCampaignId is { } campaign ? campaign.ToString("D") : DBNull.Value);

        _ = command.Parameters.AddWithValue("$session", intent.DestinationSessionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$manifest", intent.AttachmentManifestDigest.Bytes);

        _ = command.Parameters.AddWithValue("$count", intent.AttachmentManifestCount);

        _ = command.Parameters.AddWithValue("$root", destinationRootIdentityEvidence);

        _ = command.Parameters.AddWithValue("$now", now);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return intent with { Phase = ProtectedSessionTransferPhase.Prepared, Revision = 0 };

    }

    internal async Task<ProtectedSessionTransferIntentRow?> ReadAsync(
        Guid operationId,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT OperationId, EffectDigest, SourceEvidenceDigest, DestinationBindingDigest,
                   DestinationScopeCode, DestinationCampaignId, DestinationSessionId,
                   AttachmentManifestDigest, AttachmentManifestCount, PhaseCode,
                   PendingDispositionCode, Revision
            FROM protected_session_transfer_intents
            WHERE OperationId = $operation;
            """;

        _ = command.Parameters.AddWithValue("$operation", operationId.ToString("D"));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return null;

        }

        return new ProtectedSessionTransferIntentRow(
            Guid.Parse(reader.GetString(0)),
            new CovenantDigest(ReadBlob(reader, 1)),
            new CovenantDigest(ReadBlob(reader, 2)),
            new CovenantDigest(ReadBlob(reader, 3)),
            (CovenantScope)reader.GetInt32(4),
            reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            Guid.Parse(reader.GetString(6)),
            new CovenantDigest(ReadBlob(reader, 7)),
            reader.GetInt64(8),
            (ProtectedSessionTransferPhase)reader.GetInt32(9),
            reader.IsDBNull(10) ? null : (CovenantExclusiveLeaseDisposition)reader.GetInt32(10),
            reader.GetInt64(11));

    }

    /// <summary>
    /// Advances the parent journal by exactly one revision along a permitted edge.
    /// </summary>
    internal async Task<Result> AdvanceAsync(
        Guid operationId,
        long expectedRevision,
        ProtectedSessionTransferPhase phase,
        CovenantExclusiveLeaseDisposition? pendingDisposition,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        using CovenantSqliteAuthorizationScope scope = _initializer.Authorize(
            _connection,
            CovenantSqliteAuthorizationKind.ProtectedSessionTransfer);

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE protected_session_transfer_intents
            SET PhaseCode = $phase,
                PendingDispositionCode = $disposition,
                Revision = Revision + 1,
                UpdatedAtUtc = $now
            WHERE OperationId = $operation AND Revision = $expected;
            """;

        _ = command.Parameters.AddWithValue("$phase", (int)phase);

        _ = command.Parameters.AddWithValue(
            "$disposition",
            pendingDisposition is { } disposition ? (int)disposition : DBNull.Value);

        _ = command.Parameters.AddWithValue("$now", Iso(_timeProvider.GetUtcNow()));

        _ = command.Parameters.AddWithValue("$operation", operationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$expected", expectedRevision);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "A protected transfer phase advance lost its compare-and-swap."));

    }

    /// <summary>
    /// Journals every blob child in full before the first filesystem byte.
    /// </summary>
    /// <remarks>
    /// Written in advance so recovery can enumerate, verify, and compare-delete every file this
    /// operation could possibly have created without holding the source lease that produced them.
    /// </remarks>
    internal async Task PrepareBlobAsync(
        Guid operationId,
        long ordinal,
        string parentIdentityEvidence,
        string temporaryLeaf,
        string finalLeaf,
        byte[] expectedContentHash,
        long expectedContentLength,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        string now = Iso(_timeProvider.GetUtcNow());

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO protected_session_transfer_blobs (
                OperationId, BlobOrdinal, DurableParentIdentityEvidence, TemporaryLeaf, FinalLeaf,
                ExpectedContentHash, ExpectedContentLength, ObservedPhysicalIdentity, PhaseCode,
                Revision, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($operation, $ordinal, $parent, $temporary, $final,
                    $hash, $length, NULL, 1,
                    0, $now, $now);
            """;

        _ = command.Parameters.AddWithValue("$operation", operationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$ordinal", ordinal);

        _ = command.Parameters.AddWithValue("$parent", Encoding.UTF8.GetBytes(parentIdentityEvidence));

        _ = command.Parameters.AddWithValue("$temporary", Encoding.UTF8.GetBytes(temporaryLeaf));

        _ = command.Parameters.AddWithValue("$final", Encoding.UTF8.GetBytes(finalLeaf));

        _ = command.Parameters.AddWithValue("$hash", expectedContentHash);

        _ = command.Parameters.AddWithValue("$length", expectedContentLength);

        _ = command.Parameters.AddWithValue("$now", now);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Advances one blob child by exactly one phase, optionally recording its one-time identity.
    /// </summary>
    internal async Task<Result> AdvanceBlobAsync(
        Guid operationId,
        long ordinal,
        long expectedRevision,
        ProtectedSessionTransferBlobPhase phase,
        byte[]? observedPhysicalIdentity,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        using CovenantSqliteAuthorizationScope scope = _initializer.Authorize(
            _connection,
            CovenantSqliteAuthorizationKind.ProtectedSessionTransfer);

        await using SqliteCommand command = _connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE protected_session_transfer_blobs
            SET PhaseCode = $phase,
                ObservedPhysicalIdentity = COALESCE($identity, ObservedPhysicalIdentity),
                Revision = Revision + 1,
                UpdatedAtUtc = $now
            WHERE OperationId = $operation AND BlobOrdinal = $ordinal AND Revision = $expected;
            """;

        _ = command.Parameters.AddWithValue("$phase", (int)phase);

        _ = command.Parameters.AddWithValue(
            "$identity",
            observedPhysicalIdentity is null ? DBNull.Value : observedPhysicalIdentity);

        _ = command.Parameters.AddWithValue("$now", Iso(_timeProvider.GetUtcNow()));

        _ = command.Parameters.AddWithValue("$operation", operationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$ordinal", ordinal);

        _ = command.Parameters.AddWithValue("$expected", expectedRevision);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "A protected transfer blob phase advance lost its compare-and-swap."));

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
