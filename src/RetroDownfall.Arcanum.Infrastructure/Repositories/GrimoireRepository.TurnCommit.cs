using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// The one batch-aware assistant finalizer (§10.13).
/// </summary>
/// <remarks>
/// Assistant content, the one-shot finalization guard, and any staged Covenant batch commit inside a
/// single <c>BEGIN IMMEDIATE</c> transaction on the connection EF already owns. An immediate
/// transaction is required rather than preferred: two writers validating the same aggregate capacity
/// snapshot under a deferred transaction would each conclude there was room for the last version
/// slot, and only one of them would be right.
///
/// <para>The guard row, not the content, is what proves a turn finished. Inferring "unfinished" from
/// empty content is how a valid empty answer used to be mistaken for an interrupted turn, and how a
/// retry could run a second turn against a Session that already had one.</para>
/// </remarks>
public sealed partial class GrimoireRepository : IGrimoireTurnCommitter
{

    public async Task<Result<TurnCommitReceipt>> CommitTurnAsync(
        TurnCommitRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        using IDisposable writeLock = await SessionEntryPersistence
            .AcquireWriteLockAsync(request.SessionId, cancellationToken)
            .ConfigureAwait(false);

        try
        {

            return await SqliteBusyRetry.ExecuteAsync(
                () => CommitWithinImmediateTransactionAsync(request, cancellationToken),
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            _logger.LogWarning(
                exception,
                "The turn for assistant entry {AssistantEntryId} could not be finalized; nothing was published.",
                request.AssistantEntryId);

            return new Error(
                ErrorCodes.Grimoire.WriteFailed,
                "The conversation turn could not be finalized.");

        }

    }

    private async Task<Result<TurnCommitReceipt>> CommitWithinImmediateTransactionAsync(
        TurnCommitRequest request,
        CancellationToken cancellationToken)
    {

        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
            .AcquireScopedAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return Result<TurnCommitReceipt>.Failure(acquired.Error);
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        connection = lease.Connection;

        await using SqliteTransaction sqliteTransaction = connection.BeginTransaction(deferred: false);

        await using IDbContextTransaction efTransaction =
            await _db.Database.UseTransactionAsync(sqliteTransaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("EF could not attach the SQLite turn-commit transaction.");

        await GrimoireScopedConsumerTestSeam
            .PauseAsync("GrimoireRepository.CommitWithinImmediateTransactionAsync", cancellationToken)
            .ConfigureAwait(false);

        try
        {

            // Resolve the guard before anything else. A retry after a committed response has to
            // recover the durable answer rather than run a second, partial finalization.
            AssistantFinalizationOutcome? existing = await ReadFinalizationGuardAsync(
                connection,
                sqliteTransaction,
                request,
                cancellationToken).ConfigureAwait(false);

            if (existing is { } resolved)
            {

                await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Result<TurnCommitReceipt>.Success(
                    new TurnCommitReceipt(request.AssistantEntryId, resolved, Replayed: true, []));

            }

            Result applied = request.Outcome is AssistantFinalizationOutcome.Discarded
                ? await DiscardPlaceholderAsync(request, cancellationToken).ConfigureAwait(false)
                : await PersistAssistantContentAsync(request, cancellationToken).ConfigureAwait(false);

            if (applied.IsFailure)
            {

                await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return applied.Error;

            }

            ImmutableArray<CovenantMutationReceipt> receipts = [];

            if (!request.Mutations.IsEmpty)
            {

                Result<ImmutableArray<CovenantMutationReceipt>> published = await PublishCovenantBatchAsync(
                    request,
                    connection,
                    sqliteTransaction,
                    cancellationToken).ConfigureAwait(false);

                if (published.IsFailure)
                {

                    await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return published.Error;

                }

                receipts = published.Value;

            }

            Result capacity = await EnsureFinalizationCapacityAsync(
                request,
                new CovenantMutationTransaction(connection, sqliteTransaction),
                cancellationToken).ConfigureAwait(false);

            if (capacity.IsFailure)
            {

                await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return capacity.Error;

            }

            Result labelled = await LabelAssistantEntryAsync(
                request,
                connection,
                sqliteTransaction,
                cancellationToken).ConfigureAwait(false);

            if (labelled.IsFailure)
            {

                await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return labelled.Error;

            }

            await InsertFinalizationGuardAsync(
                connection,
                sqliteTransaction,
                request,
                cancellationToken).ConfigureAwait(false);

            await efTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<TurnCommitReceipt>.Success(
                new TurnCommitReceipt(
                    request.AssistantEntryId,
                    request.Outcome,
                    Replayed: false,
                    receipts));

        }
        catch
        {

            await efTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

    }

    /// <summary>
    /// Writes the assistant entry's information-flow label inside the finalization transaction.
    /// </summary>
    /// <remarks>
    /// Same transaction as the content, deliberately: a Covenant-derived response whose label did
    /// not land would be protected content sitting in the Grimoire with nothing recording that it is
    /// protected, and every reader downstream — cache filters, exports, generic search — would treat
    /// it as ordinary. A label failure therefore fails the whole finalization rather than degrading
    /// (§10.12).
    ///
    /// <para>A discarded placeholder is not labelled. There is no durable artifact left to describe,
    /// and a label pointing at a deleted entry would keep the Session projection tainted for content
    /// nobody can read.</para>
    /// </remarks>
    private static async Task<Result> LabelAssistantEntryAsync(
        TurnCommitRequest request,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        if (request.Outcome is AssistantFinalizationOutcome.Discarded)
        {

            return Result.Success();

        }

        Result<LabeledArtifactWriteReceipt> labelled = await ArtifactSensitivityLedger.WriteWithinAsync(
            connection,
            transaction,
            request.ToDerivedArtifactWrite(),
            cancellationToken).ConfigureAwait(false);

        return labelled.IsFailure ? labelled.Error : Result.Success();

    }

    private async Task<Result> PersistAssistantContentAsync(
        TurnCommitRequest request,
        CancellationToken cancellationToken)
    {

        int updated = await _db.Entries
            .Where(entry => entry.Id == request.AssistantEntryId
                && entry.Role == MessageRole.Assistant)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(entry => entry.Content, request.FinalText),
                cancellationToken).ConfigureAwait(false);

        return updated == 0
            ? Result.Failure(new Error(
                ErrorCodes.Grimoire.WriteFailed,
                "No assistant placeholder matched this finalization."))
            : Result.Success();

    }

    /// <summary>
    /// Removes an untouched placeholder, and refuses to discard one that already carries content.
    /// </summary>
    /// <remarks>
    /// Refusing is the point. Deleting a placeholder that already holds streamed text would remove a
    /// response the operator may already have read, and writing a <c>Discarded</c> guard while
    /// leaving the text in place would make the durable outcome contradict the durable content. A
    /// turn that produced partial text finalizes with that text instead.
    /// </remarks>
    private async Task<Result> DiscardPlaceholderAsync(
        TurnCommitRequest request,
        CancellationToken cancellationToken)
    {

        int deleted = await _db.Entries
            .Where(entry => entry.Id == request.AssistantEntryId
                && entry.Role == MessageRole.Assistant
                && entry.Content == string.Empty)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        if (deleted > 0)
        {

            await _entryPersistence
                .DecrementUnsummarizedEntryCountIfKnownAsync(request.SessionId, 1, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();

        }

        bool holdsContent = await _db.Entries
            .AsNoTracking()
            .AnyAsync(
                entry => entry.Id == request.AssistantEntryId && entry.Content != string.Empty,
                cancellationToken).ConfigureAwait(false);

        return holdsContent
            ? Result.Failure(new Error(
                ErrorCodes.Grimoire.WriteFailed,
                "An assistant entry that already carries content cannot be discarded."))
            : Result.Success();

    }

    private async Task<Result<ImmutableArray<CovenantMutationReceipt>>> PublishCovenantBatchAsync(
        TurnCommitRequest request,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        if (_covenantKernel is null)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant mutation kernel is not composed in this host.");

        }

        CovenantMutationBatchBinding binding = request.MutationBinding!;

        CovenantMutationBatch batch = new(
            binding.DatasetGeneration,
            binding.ExpectedKeyReclamationEpoch,
            binding.ExpectedCampaignRegistryEpoch,
            DateTimeOffset.UtcNow,
            request.Mutations);

        Result<IReadOnlyList<CovenantMutationReceipt>> published = await _covenantKernel
            .ApplyBatchAsync(
                batch,
                new CovenantMutationTransaction(connection, transaction),
                cancellationToken).ConfigureAwait(false);

        return published.IsFailure
            ? published.Error
            : Result<ImmutableArray<CovenantMutationReceipt>>.Success([.. published.Value]);

    }

    /// <summary>
    /// Proves this assistant identity owns a consumed finalization-guard slot.
    /// </summary>
    /// <remarks>
    /// Guard capacity is durable rather than advisory, so the guard insert is refused outright
    /// without one. A public claim reserves its slot before any provider work starts; an internal
    /// begin has no claim and no waiting period, so it allocates an already-consumed slot beside its
    /// finalization instead. The reservation identity is derived from the assistant entry rather
    /// than generated, which is what makes a retry consume the slot it already owns instead of
    /// colliding with itself on the one-reservation-per-assistant index.
    /// </remarks>
    private async Task<Result> EnsureFinalizationCapacityAsync(
        TurnCommitRequest request,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await SeedSessionCapacityRowAsync(request.SessionId, transaction, cancellationToken)
            .ConfigureAwait(false);

        Result<AssistantFinalizationCapacityReservation> allocated = await _finalizationCapacity
            .AllocateDirectFinalizationAsync(
                new DirectFinalizationCapacityRequest(
                    DeriveReservationId(request.AssistantEntryId),
                    request.SessionId,
                    request.AssistantEntryId,
                    AssistantFinalizationCapacityOrigin.Internal),
                transaction,
                cancellationToken).ConfigureAwait(false);

        return allocated.IsFailure ? allocated.Error : Result.Success();

    }

    /// <summary>
    /// Creates this Session's zeroed capacity counter row if it has none.
    /// </summary>
    /// <remarks>
    /// A backfill, not a bypass. Every counter move is still a checked update against the ceilings;
    /// this only gives a Session created before the ledger existed — or through a path that predates
    /// it — the row those checks are applied to. Insert-or-ignore rather than a read-then-write, so
    /// two concurrent finalizations on one Session cannot both decide the row is missing.
    /// </remarks>
    private static async Task SeedSessionCapacityRowAsync(
        Guid sessionId,
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO session_turn_quota_state (
                SessionId,
                ClaimCount,
                ReservedFinalizationCount,
                ConsumedFinalizationCount)
            VALUES ($sessionId, 0, 0, 0);
            """;

        _ = command.Parameters.AddWithValue("$sessionId", sessionId);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Derives the stable reservation identity for one assistant entry.
    /// </summary>
    private static Guid DeriveReservationId(Guid assistantEntryId)
    {

        Span<byte> preimage = stackalloc byte[16 + AssistantFinalizationReservationDomain.Length];

        AssistantFinalizationReservationDomain.CopyTo(preimage);

        _ = assistantEntryId.TryWriteBytes(preimage[AssistantFinalizationReservationDomain.Length..]);

        Span<byte> digest = stackalloc byte[32];

        _ = System.Security.Cryptography.SHA256.HashData(preimage, digest);

        return new Guid(digest[..16]);

    }

    private static ReadOnlySpan<byte> AssistantFinalizationReservationDomain =>
        "Arcanum.Grimoire.FinalizationReservation.v1\0"u8;

    /// <summary>
    /// Reads the one-shot guard, failing closed when the same entry is replayed with a different
    /// request.
    /// </summary>
    private static async Task<AssistantFinalizationOutcome?> ReadFinalizationGuardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TurnCommitRequest request,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT OutcomeCode, RequestDigest
            FROM assistant_entry_finalizations
            WHERE AssistantEntryId = $assistantEntryId;
            """;

        _ = command.Parameters.AddWithValue("$assistantEntryId", request.AssistantEntryId);

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        byte[] storedDigest = (byte[])reader.GetValue(1);

        if (!storedDigest.AsSpan().SequenceEqual(request.RequestDigest.Bytes.AsSpan()))
        {

            throw new InvalidOperationException(
                "This assistant entry was already finalized for a different request.");

        }

        return (AssistantFinalizationOutcome)reader.GetInt64(0);

    }

    private static async Task InsertFinalizationGuardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TurnCommitRequest request,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO assistant_entry_finalizations (
                AssistantEntryId,
                SessionId,
                OutcomeCode,
                ContentSensitivityCode,
                ContentSensitivityDigest,
                RequestDigest,
                FinalReceiptDigest,
                SourceEvidenceDigest,
                FinalizedAtUtc)
            VALUES (
                $assistantEntryId,
                $sessionId,
                $outcomeCode,
                $sensitivityCode,
                $sensitivityDigest,
                $requestDigest,
                $finalReceiptDigest,
                NULL,
                $finalizedAtUtc);
            """;

        _ = command.Parameters.AddWithValue("$assistantEntryId", request.AssistantEntryId);

        _ = command.Parameters.AddWithValue("$sessionId", request.SessionId);

        _ = command.Parameters.AddWithValue("$outcomeCode", (long)request.Outcome);

        _ = command.Parameters.AddWithValue("$sensitivityCode", (long)request.ContentSensitivity);

        _ = command.Parameters.AddWithValue(
            "$sensitivityDigest",
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                request.ContentSensitivity,
                request.ContentProvenance.Mode,
                request.ContentProvenance.ExactGenerationIds,
                request.ContentProvenance.BloomBits)).Bytes);

        _ = command.Parameters.AddWithValue("$requestDigest", request.RequestDigest.Bytes);

        _ = command.Parameters.AddWithValue(
            "$finalReceiptDigest",
            request.FinalReceiptDigest is { } receipt ? receipt.Bytes : DBNull.Value);

        _ = command.Parameters.AddWithValue(
            "$finalizedAtUtc",
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

}
