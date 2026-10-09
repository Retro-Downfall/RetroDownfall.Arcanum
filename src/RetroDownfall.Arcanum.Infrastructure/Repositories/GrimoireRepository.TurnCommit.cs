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

        // Read from the latch before BEGIN and disposed only after the transaction ends. A latch read is
        // never credential I/O, so taking it under the turn's lease reads no keychain there either.
        using CovenantAgentErasureGate erasureGate = request.Mutations.IsEmpty || _covenantKernel is null
            ? CovenantAgentErasureGate.None
            : _covenantKernel.CaptureErasureGate();

        await using SqliteTransaction sqliteTransaction = connection.BeginTransaction(deferred: false);

        await using IDbContextTransaction efTransaction =
            await _db.Database.UseTransactionAsync(sqliteTransaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("EF could not attach the SQLite turn-commit transaction.");

        try
        {
            // Resolve the guard before anything else. A retry after a committed response has to
            // recover the durable answer rather than run a second, partial finalization.
            FinalizationGuard? existing = await ReadFinalizationGuardAsync(
                connection,
                sqliteTransaction,
                request,
                cancellationToken).ConfigureAwait(false);

            if (existing is { } resolved)
            {
                await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                await PauseAfterTurnTransactionAsync(
                    GrimoireScopedConsumerFinalUseKind.TransactionRolledBack,
                    resolved.Outcome,
                    cancellationToken).ConfigureAwait(false);

                return Result<TurnCommitReceipt>.Success(
                    new TurnCommitReceipt(
                        request.AssistantEntryId,
                        resolved.Outcome,
                        Replayed: true,
                        [],
                        resolved.ThroughEntrySequence));
            }

            Result applied = request.Outcome is AssistantFinalizationOutcome.Discarded
                ? await DiscardPlaceholderAsync(request, cancellationToken).ConfigureAwait(false)
                : await PersistAssistantContentAsync(request, cancellationToken).ConfigureAwait(false);

            if (applied.IsFailure)
            {
                await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                await PauseAfterTurnTransactionAsync(
                    GrimoireScopedConsumerFinalUseKind.TransactionRolledBack,
                    request.Outcome,
                    cancellationToken).ConfigureAwait(false);

                return applied.Error;
            }

            ImmutableArray<CovenantMutationReceipt> receipts = [];

            if (!request.Mutations.IsEmpty)
            {
                Result<ImmutableArray<CovenantMutationReceipt>> published = await PublishCovenantBatchAsync(
                    request,
                    connection,
                    sqliteTransaction,
                    erasureGate,
                    cancellationToken).ConfigureAwait(false);

                if (published.IsFailure)
                {
                    await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    await PauseAfterTurnTransactionAsync(
                        GrimoireScopedConsumerFinalUseKind.TransactionRolledBack,
                        request.Outcome,
                        cancellationToken).ConfigureAwait(false);

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

                await PauseAfterTurnTransactionAsync(
                    GrimoireScopedConsumerFinalUseKind.TransactionRolledBack,
                    request.Outcome,
                    cancellationToken).ConfigureAwait(false);

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

                await PauseAfterTurnTransactionAsync(
                    GrimoireScopedConsumerFinalUseKind.TransactionRolledBack,
                    request.Outcome,
                    cancellationToken).ConfigureAwait(false);

                return labelled.Error;
            }

            long? throughEntrySequence = await ReadLatestEntrySequenceAsync(
                request.SessionId,
                cancellationToken).ConfigureAwait(false);

            await InsertFinalizationGuardAsync(
                connection,
                sqliteTransaction,
                request,
                throughEntrySequence,
                cancellationToken).ConfigureAwait(false);

            if (request.ClaimInterruption is { } interruption)
            {
                Result interrupted = await RecordClaimInterruptionAsync(
                    connection, sqliteTransaction, interruption, cancellationToken).ConfigureAwait(false);

                if (interrupted.IsFailure)
                {
                    await efTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    await PauseAfterTurnTransactionAsync(
                        GrimoireScopedConsumerFinalUseKind.TransactionRolledBack,
                        request.Outcome,
                        cancellationToken).ConfigureAwait(false);

                    return interrupted.Error;
                }
            }

            await efTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            // A published batch advanced the canonical search sequence. Republished after COMMIT, on the
            // connection that committed and under the turn's Covenant lease, and before the final use.
            if (!request.Mutations.IsEmpty && _availabilityRepublisher is { } republisher)
            {
                await republisher
                    .RepublishAsync(connection, CovenantHealthTransition.CanonicalMutation)
                    .ConfigureAwait(false);
            }

            await PauseAfterTurnTransactionAsync(
                GrimoireScopedConsumerFinalUseKind.TransactionCommitted,
                request.Outcome,
                cancellationToken).ConfigureAwait(false);

            return Result<TurnCommitReceipt>.Success(
                new TurnCommitReceipt(
                    request.AssistantEntryId,
                    request.Outcome,
                    Replayed: false,
                    receipts,
                    throughEntrySequence));
        }
        catch
        {
            await efTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            await PauseAfterTurnTransactionAsync(
                GrimoireScopedConsumerFinalUseKind.TransactionRolledBack,
                request.Outcome,
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    private async Task<long?> ReadLatestEntrySequenceAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT MAX("Sequence")
            FROM "Entries"
            WHERE "SessionId" = $sessionId;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(
            command,
            "$sessionId",
            GrimoireEntitySql.Format(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static ValueTask PauseAfterTurnTransactionAsync(
        GrimoireScopedConsumerFinalUseKind kind,
        AssistantFinalizationOutcome outcome,
        CancellationToken cancellationToken) =>
        GrimoireScopedConsumerTestSeam.PauseAsync(
            "GrimoireRepository.CommitWithinImmediateTransactionAsync",
            kind,
            (int)outcome,
            cancellationToken);

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
        int updated = await ExecuteNonQueryAsync(
            """
            UPDATE "Entries"
            SET "Content" = $content
            WHERE "Id" = $entryId
              AND "Role" = $assistantRole;
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$content", request.FinalText);
                GrimoireEntitySql.AddParameter(
                    command,
                    "$entryId",
                    GrimoireEntitySql.Format(request.AssistantEntryId));
                GrimoireEntitySql.AddParameter(command, "$assistantRole", (int)MessageRole.Assistant);
            },
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
        int deleted = await ExecuteNonQueryAsync(
            """
            DELETE FROM "Entries"
            WHERE "Id" = $entryId
              AND "Role" = $assistantRole
              AND "Content" = '';
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(
                    command,
                    "$entryId",
                    GrimoireEntitySql.Format(request.AssistantEntryId));
                GrimoireEntitySql.AddParameter(command, "$assistantRole", (int)MessageRole.Assistant);
            },
            cancellationToken).ConfigureAwait(false);

        if (deleted > 0)
        {
            await _entryPersistence
                .DecrementUnsummarizedEntryCountIfKnownAsync(request.SessionId, 1, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success();
        }

        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT 1
            FROM "Entries"
            WHERE "Id" = $entryId
              AND "Content" <> ''
            LIMIT 1;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(
            command,
            "$entryId",
            GrimoireEntitySql.Format(request.AssistantEntryId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        bool holdsContent = value is not null and not DBNull;

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
        CovenantAgentErasureGate erasureGate,
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
                erasureGate,
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
        await using (SqliteCommand command = transaction.CreateCommand())
        {
            command.CommandText = """
                SELECT reservation.OriginCode,
                    CASE WHEN reservation.StateCode = 2 AND reservation.SessionId = $session
                        AND EXISTS (SELECT 1 FROM session_turn_claims claim
                            WHERE claim.ClaimId = reservation.ClaimId AND claim.StateCode = 2
                                AND claim.SessionId = reservation.SessionId
                                AND claim.FinalizationReservationId = reservation.ReservationId
                                AND claim.AssistantEntryId = reservation.AssistantEntryId
                                AND claim.RequestDigest = $request)
                    THEN 1 ELSE 0 END
                FROM assistant_finalization_capacity_reservations reservation
                WHERE reservation.AssistantEntryId = $assistant;
                """;

            _ = command.Parameters.AddWithValue("$session", request.SessionId.ToString("D").ToUpperInvariant());

            _ = command.Parameters.AddWithValue("$assistant", request.AssistantEntryId.ToString("D").ToUpperInvariant());

            _ = command.Parameters.AddWithValue("$request", request.RequestDigest.Bytes.ToArray());

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && reader.GetInt64(0) == (long)AssistantFinalizationCapacityOrigin.PublicClaim)
            {
                return reader.GetInt64(1) == 1
                    ? Result.Success()
                    : new Error(ErrorCodes.Covenant.StaleSnapshot,
                        "This public assistant finalization does not match its consumed turn claim reservation.");
            }
        }

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
    private static async Task<FinalizationGuard?> ReadFinalizationGuardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TurnCommitRequest request,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT OutcomeCode, RequestDigest, ThroughEntrySequence
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

        return new FinalizationGuard(
            (AssistantFinalizationOutcome)reader.GetInt64(0),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static async Task InsertFinalizationGuardAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TurnCommitRequest request,
        long? throughEntrySequence,
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
                FinalizedAtUtc,
                ThroughEntrySequence)
            VALUES (
                $assistantEntryId,
                $sessionId,
                $outcomeCode,
                $sensitivityCode,
                $sensitivityDigest,
                $requestDigest,
                $finalReceiptDigest,
                NULL,
                $finalizedAtUtc,
                $throughEntrySequence);
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
            UtcInstantText.Format(DateTimeOffset.UtcNow));

        _ = command.Parameters.AddWithValue(
            "$throughEntrySequence",
            throughEntrySequence is { } sequence ? sequence : DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record FinalizationGuard(
        AssistantFinalizationOutcome Outcome,
        long? ThroughEntrySequence);

    private static async Task<Result> RecordClaimInterruptionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionTurnClaimInterruption interruption,
        CancellationToken cancellationToken)
    {
        SessionTurnClaimLease lease = interruption.Lease;

        SessionTurnClaimOutcome outcome = interruption.Outcome;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // Retaining paid partial text must never create the crash window in which native Committed
        // evidence is recovered as a successful request. Both outcomes share this transaction, and
        // the writer must still own the exact claim it began before either one can publish.
        command.CommandText = """
            UPDATE session_turn_claims
            SET StateCode = 6, TerminalErrorCode = $code, TerminalHttpStatus = $status,
                TerminalParameterBytes = $parameters, TerminalParameterDigest = $digest,
                TerminalAtUtc = $now, HeartbeatAtUtc = $now, ExecutorId = NULL, LeaseDeadlineUtc = NULL
            WHERE ClaimId = $claim AND SessionId = $session AND StateCode = 2
                AND OriginInstallationId = $origin AND OriginRestoreEpoch = $epoch
                AND ClientTurnId = $client AND SurfaceCode = $surface
                AND RequestDigest = $request AND DependencyDigest = $dependency
                AND PreRequestHistoryRevision = $history AND PreRequestHistoryWatermarkUtc IS $watermark
                AND InputSensitivityRevision = $inputSensitivity AND FinalizationReservationId = $reservation
                AND AssistantEntryId = $assistant AND OwnerBootId = $boot AND ExecutorId = $executor
                AND EXISTS (
                    SELECT 1 FROM assistant_finalization_capacity_reservations reservation
                    WHERE reservation.ReservationId = session_turn_claims.FinalizationReservationId
                        AND reservation.SessionId = session_turn_claims.SessionId
                        AND reservation.ClaimId = session_turn_claims.ClaimId AND reservation.OriginCode = 1
                        AND reservation.StateCode = 2 AND reservation.AssistantEntryId = $assistant)
                AND EXISTS (
                    SELECT 1 FROM assistant_entry_finalizations final
                    WHERE final.AssistantEntryId = $assistant AND final.SessionId = $session
                        AND final.RequestDigest = $request AND final.OutcomeCode = 1 AND final.SourceEvidenceDigest IS NULL);
            """;

        _ = command.Parameters.AddWithValue("$claim", ClaimGuid(lease.Claim.ClaimId));

        _ = command.Parameters.AddWithValue("$session", ClaimGuid(lease.Claim.SessionId));

        _ = command.Parameters.AddWithValue("$origin", ClaimGuid(lease.Claim.OriginInstallationId));

        _ = command.Parameters.AddWithValue("$epoch", lease.Claim.OriginRestoreEpoch);

        _ = command.Parameters.AddWithValue("$client", ClaimGuid(lease.Claim.ClientTurnId));

        _ = command.Parameters.AddWithValue("$surface", (int)lease.Claim.Surface);

        _ = command.Parameters.AddWithValue("$request", lease.Claim.RequestDigest.Bytes);

        _ = command.Parameters.AddWithValue("$dependency", lease.Claim.DependencyDigest.Bytes);

        _ = command.Parameters.AddWithValue("$history", lease.Claim.PreRequestHistoryRevision);

        _ = command.Parameters.AddWithValue("$watermark", lease.Claim.PreRequestHistoryWatermarkUtc is { } watermark ? UtcInstantText.Format(watermark) : DBNull.Value);

        _ = command.Parameters.AddWithValue("$inputSensitivity", lease.Claim.InputSensitivityRevision);

        _ = command.Parameters.AddWithValue("$reservation", ClaimGuid(lease.Claim.FinalizationReservationId));

        _ = command.Parameters.AddWithValue("$assistant", ClaimGuid(lease.FutureAssistantEntryId));

        _ = command.Parameters.AddWithValue("$boot", ClaimGuid(lease.OwnerBootId!.Value));

        _ = command.Parameters.AddWithValue("$executor", ClaimGuid(lease.ExecutorId!.Value));

        _ = command.Parameters.AddWithValue("$code", outcome.TerminalErrorCode!);

        _ = command.Parameters.AddWithValue("$status", outcome.TerminalHttpStatus!.Value);

        _ = command.Parameters.AddWithValue("$parameters", outcome.TerminalParameterBytes.ToArray());

        _ = command.Parameters.AddWithValue("$digest", outcome.TerminalParameterDigest!.Value.Bytes);

        _ = command.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? Result.Success()
            : Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "The interrupted reply no longer owns its exact Session claim."));
    }
}
