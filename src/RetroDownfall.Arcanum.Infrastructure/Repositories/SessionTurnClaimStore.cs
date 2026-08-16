using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// The SQLCipher-backed durable turn-claim layer, over raw parameterized commands.
/// </summary>
/// <remarks>
/// Every method runs in exactly one <c>BEGIN IMMEDIATE</c> transaction and commits once. Reading a
/// claim, checking Session liveness, reserving capacity, and inserting the row are a single unit
/// because each of them decides whether the others are allowed: a claim read outside the write lock
/// would let two requests both conclude the Session was free and both insert, and the losing insert
/// would already have moved the quota counters.
///
/// <para>Busy retry wraps the whole transaction rather than any statement inside it. Retrying part of
/// a transaction is not a retry, it is a second, partial transaction.</para>
///
/// <para>The future assistant Entry identity is minted once, at acquisition, and stored on the
/// reserved finalization capacity — the schema deliberately leaves
/// <c>session_turn_claims.AssistantEntryId</c> null until the placeholder actually exists. Every
/// resume, adoption, and replay reads that same reserved identity back and never generates a second
/// one, which is what stops one turn from producing two assistant Entries.</para>
/// </remarks>
internal sealed class SessionTurnClaimStore(
    ICovenantConnectionSource connections,
    CovenantQuotaGuard quotas,
    Guid bootId) : ISessionTurnClaimCoordinator
{

    /// <summary>
    /// How long an acquisition owns its claim before another boot may take it over. Code-owned: a
    /// caller that could choose its own fencing window could choose never to be fenced.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private const string ClaimColumns = """
        ClaimId, OriginInstallationId, OriginRestoreEpoch, ClientTurnId, SessionId, SurfaceCode,
        RequestDigest, DependencyDigest, StateCode, PreRequestHistoryWatermarkUtc,
        PreRequestHistoryRevision, InputSensitivityRevision, ExpectedCurrentSensitivityRevision,
        FinalizationReservationId, UserEntryId, AssistantEntryId, OwnerBootId, ExecutorId,
        LeaseDeadlineUtc, CheckpointRevision, CompletedStepMask, TerminalErrorCode,
        TerminalHttpStatus, TerminalParameterBytes, CreatedAtUtc
        """;

    public async ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(
        SessionTurnRequestIdentity request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {

            return invalid;

        }

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await SqliteBusyRetry.ExecuteAsync(
            () => AcquireWithinTransactionAsync(connection, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    }

    public async ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(
        SessionTurnClaimLease lease,
        AssistantReplyBeginReceipt begin,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(begin);

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await SqliteBusyRetry.ExecuteAsync(
            () => MarkBegunWithinTransactionAsync(connection, lease, begin, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    }

    public async ValueTask<Result<SessionTurnClaim>> CompleteAsync(
        SessionTurnClaimLease lease,
        SessionTurnClaimOutcome outcome,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(lease);

        ArgumentNullException.ThrowIfNull(outcome);

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await SqliteBusyRetry.ExecuteAsync(
            () => CompleteWithinTransactionAsync(connection, lease, outcome, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<SessionTurnClaimLease>> AcquireWithinTransactionAsync(
        SqliteConnection connection,
        SessionTurnRequestIdentity request,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        CovenantMutationTransaction owned = new(connection, transaction);

        ClaimRow? existing = await ReadByClientIdentityAsync(
                owned,
                request.OriginInstallationId,
                request.ClientTurnId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is { } found)
        {

            Result<SessionTurnClaimLease> resumed = await ResumeAsync(owned, request, found, cancellationToken)
                .ConfigureAwait(false);

            if (resumed.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return resumed.Error;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return resumed;

        }

        // A different client turn ID while this Session already has live work is the case the partial
        // unique active-claim index exists for. Naming it here turns a raw constraint abort into the
        // typed answer a competing client is supposed to receive.
        if (await HasLiveClaimAsync(owned, request.SessionId, cancellationToken).ConfigureAwait(false))
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(
                ErrorCodes.Hub.SessionTurnBusy,
                "This Session already has a turn in flight, so a second one cannot claim it.");

        }

        Guid claimId = Guid.NewGuid();

        Guid reservationId = Guid.NewGuid();

        Guid futureAssistantEntryId = Guid.NewGuid();

        Result<AssistantFinalizationCapacityReservation> reserved = await quotas
            .ReserveClaimAndFinalizationAsync(
                new SessionTurnCapacityReservationRequest(
                    reservationId,
                    request.SessionId,
                    futureAssistantEntryId,
                    claimId),
                owned,
                cancellationToken)
            .ConfigureAwait(false);

        if (reserved.IsFailure)
        {

            // Nothing is committed, so a capacity refusal leaves no claim, no reservation, no counter
            // movement, and no disclosure subject behind.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return reserved.Error;

        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid executorId = Guid.NewGuid();

        Result inserted = await InsertClaimAsync(
                owned,
                request,
                claimId,
                reservationId,
                executorId,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        if (inserted.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return inserted.Error;

        }

        ClaimRow? created = await ReadByClaimIdAsync(owned, claimId, cancellationToken).ConfigureAwait(false);

        if (created is not { } row)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The inserted session turn claim could not be read back inside its own transaction.");

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result<SessionTurnClaimLease>.Success(
            new SessionTurnClaimLease(
                row.Claim,
                SessionTurnClaimDisposition.Created,
                futureAssistantEntryId,
                row.ExecutorId,
                row.Claim.OwnerBootId,
                row.LeaseDeadlineUtc));

    }

    /// <summary>
    /// Authenticates a second request against the claim it already created, then resumes, adopts, or
    /// replays it.
    /// </summary>
    /// <remarks>
    /// A terminal claim is authenticated on the stable request digest alone. It already has a durable
    /// answer, so requiring its mutable provider, model, Prompt, Spell, attachment, path, or tool
    /// policy dependencies to still be current would turn an answer into a conflict for reasons that
    /// have nothing to do with the request.
    /// </remarks>
    private async ValueTask<Result<SessionTurnClaimLease>> ResumeAsync(
        CovenantMutationTransaction transaction,
        SessionTurnRequestIdentity request,
        ClaimRow existing,
        CancellationToken cancellationToken)
    {

        if (existing.Claim.SessionId != request.SessionId
            || existing.Claim.Surface != request.Surface
            || !existing.Claim.RequestDigest.Equals(request.RequestDigest))
        {

            return new Error(
                ErrorCodes.Security.IdempotencyConflict,
                "This client turn ID is already bound to a different request.");

        }

        Guid futureAssistantEntryId = await ReadReservedAssistantEntryAsync(
                transaction,
                existing.Claim.FinalizationReservationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing.Claim.IsTerminal)
        {

            return Result<SessionTurnClaimLease>.Success(
                new SessionTurnClaimLease(
                    existing.Claim,
                    SessionTurnClaimDisposition.Replayed,
                    futureAssistantEntryId,
                    ExecutorId: null,
                    existing.Claim.OwnerBootId,
                    LeaseDeadlineUtc: null));

        }

        // Nonterminal work is about to keep running, so the plan it was frozen under still has to
        // hold. Continuing against a changed dependency would dispatch a provider call the claim was
        // never admitted for.
        if (!existing.Claim.DependencyDigest.Equals(request.DependencyDigest))
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "The execution dependencies this turn was planned under changed before it could resume.");

        }

        SessionTurnClaimDisposition disposition = existing.Claim.OwnerBootId == bootId
            ? SessionTurnClaimDisposition.Resumed
            : SessionTurnClaimDisposition.Adopted;

        DateTimeOffset now = DateTimeOffset.UtcNow;

        DateTimeOffset deadline = now + LeaseDuration;

        Guid executorId = Guid.NewGuid();

        await using SqliteCommand command = transaction.CreateCommand();

        // The state predicate is the fence: a claim that terminalized between the read and this
        // statement is refused rather than reopened.
        command.CommandText = """
            UPDATE session_turn_claims
            SET ExecutorId = $executor, OwnerBootId = $boot, LeaseDeadlineUtc = $deadline,
                HeartbeatAtUtc = $now
            WHERE ClaimId = $claim AND StateCode IN (1, 2);
            """;

        Bind(command, "$executor", executorId);

        Bind(command, "$boot", bootId);

        Bind(command, "$deadline", Iso(deadline));

        Bind(command, "$now", Iso(now));

        Bind(command, "$claim", existing.Claim.ClaimId);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This session turn claim reached a terminal state before it could be resumed.");

        }

        return Result<SessionTurnClaimLease>.Success(
            new SessionTurnClaimLease(
                existing.Claim with { OwnerBootId = bootId },
                disposition,
                futureAssistantEntryId,
                executorId,
                bootId,
                deadline));

    }

    private async Task<Result<SessionTurnClaim>> MarkBegunWithinTransactionAsync(
        SqliteConnection connection,
        SessionTurnClaimLease lease,
        AssistantReplyBeginReceipt begin,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        CovenantMutationTransaction owned = new(connection, transaction);

        ClaimRow? found = await ReadByClaimIdAsync(owned, lease.Claim.ClaimId, cancellationToken)
            .ConfigureAwait(false);

        if (found is not { } existing)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(ErrorCodes.Covenant.NotFound, "There is no session turn claim with that identity.");

        }

        Guid reservedAssistantEntryId = await ReadReservedAssistantEntryAsync(
                owned,
                existing.Claim.FinalizationReservationId,
                cancellationToken)
            .ConfigureAwait(false);

        Error? refusal = RefuseBegin(lease, begin, existing, reservedAssistantEntryId);

        if (refusal is { } error)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return error;

        }

        // A repeated begin for the exact same Entries is the same durable fact, not a second one.
        if (existing.Claim.State == SessionTurnClaimState.Begun)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Result<SessionTurnClaim>.Success(existing.Claim);

        }

        await using (SqliteCommand command = owned.CreateCommand())
        {

            command.CommandText = """
                UPDATE session_turn_claims
                SET StateCode = 2, UserEntryId = $user, AssistantEntryId = $assistant, HeartbeatAtUtc = $now
                WHERE ClaimId = $claim AND StateCode = 1 AND ExecutorId = $executor;
                """;

            Bind(command, "$user", begin.UserEntryId);

            Bind(command, "$assistant", reservedAssistantEntryId);

            Bind(command, "$now", Iso(DateTimeOffset.UtcNow));

            Bind(command, "$claim", existing.Claim.ClaimId);

            Bind(command, "$executor", lease.ExecutorId is { } executor ? executor : DBNull.Value);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "This session turn claim changed owner or state before assistant begin could record it.");

            }

        }

        return await CommitAndReadAsync(owned, transaction, existing.Claim.ClaimId, cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<Result<SessionTurnClaim>> CompleteWithinTransactionAsync(
        SqliteConnection connection,
        SessionTurnClaimLease lease,
        SessionTurnClaimOutcome outcome,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        CovenantMutationTransaction owned = new(connection, transaction);

        ClaimRow? found = await ReadByClaimIdAsync(owned, lease.Claim.ClaimId, cancellationToken)
            .ConfigureAwait(false);

        if (found is not { } existing)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(ErrorCodes.Covenant.NotFound, "There is no session turn claim with that identity.");

        }

        if (existing.Claim.IsTerminal)
        {

            return await CompleteTerminalAsync(owned, transaction, existing, outcome, cancellationToken)
                .ConfigureAwait(false);

        }

        if (RefuseLiveEdge(existing.Claim.State, outcome.State) is { } refusal)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return refusal;

        }

        // A claim that never began will never reach the transaction that consumes its guard slot, so
        // this is the only place that slot can be handed back.
        if (existing.Claim.State == SessionTurnClaimState.PendingMaintenance)
        {

            Guid reservedAssistantEntryId = await ReadReservedAssistantEntryAsync(
                    owned,
                    existing.Claim.FinalizationReservationId,
                    cancellationToken)
                .ConfigureAwait(false);

            Result<AssistantFinalizationCapacityReservation> released = await quotas
                .ReleaseReservedFinalizationAsync(
                    new AssistantFinalizationCapacityIdentity(
                        existing.Claim.FinalizationReservationId,
                        existing.Claim.SessionId,
                        reservedAssistantEntryId),
                    owned,
                    cancellationToken)
                .ConfigureAwait(false);

            if (released.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return released.Error;

            }

        }

        await using (SqliteCommand command = owned.CreateCommand())
        {

            // Clearing the executor and deadline is not tidiness: the schema refuses to keep either on
            // a terminal claim, because an expired owner could otherwise renew a lease and resume a
            // turn that already has an answer.
            command.CommandText = """
                UPDATE session_turn_claims
                SET StateCode = $state, TerminalErrorCode = $code, TerminalHttpStatus = $status,
                    TerminalParameterBytes = $parameters, TerminalParameterDigest = $digest,
                    TerminalAtUtc = $now, HeartbeatAtUtc = $now, ExecutorId = NULL, LeaseDeadlineUtc = NULL
                WHERE ClaimId = $claim AND StateCode = $expected AND ExecutorId = $executor;
                """;

            BindOutcome(command, outcome);

            Bind(command, "$now", Iso(DateTimeOffset.UtcNow));

            Bind(command, "$claim", existing.Claim.ClaimId);

            Bind(command, "$expected", (int)existing.Claim.State);

            Bind(command, "$executor", lease.ExecutorId is { } executor ? executor : DBNull.Value);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "This session turn claim changed owner or state before it could be terminalized.");

            }

        }

        return await CommitAndReadAsync(owned, transaction, existing.Claim.ClaimId, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Answers a terminalization aimed at a claim that already has an answer.
    /// </summary>
    /// <remarks>
    /// Exactly two things may happen. Repeating the identical outcome is the same durable answer, so
    /// it succeeds without a second write. Committed advancing to Erased is the one real edge out of a
    /// terminal state, and it changes the answer from content to an erasure tombstone and never back.
    /// Anything else is a claim being asked to have two answers.
    /// </remarks>
    private static async Task<Result<SessionTurnClaim>> CompleteTerminalAsync(
        CovenantMutationTransaction transaction,
        SqliteTransaction owner,
        ClaimRow existing,
        SessionTurnClaimOutcome outcome,
        CancellationToken cancellationToken)
    {

        if (existing.Claim.State == outcome.State
            && string.Equals(
                existing.Claim.Outcome?.TerminalErrorCode,
                outcome.TerminalErrorCode,
                StringComparison.Ordinal))
        {

            await owner.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Result<SessionTurnClaim>.Success(existing.Claim);

        }

        if (existing.Claim.State != SessionTurnClaimState.Committed
            || outcome.State != SessionTurnClaimState.Erased)
        {

            await owner.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This session turn claim already recorded a different terminal answer.");

        }

        await using (SqliteCommand command = transaction.CreateCommand())
        {

            // Deliberately narrow. A committed claim refuses any change to its owner, heartbeat,
            // checkpoint revision, or step mask, so the tombstone edge moves the state and its
            // timestamp and nothing else.
            command.CommandText = """
                UPDATE session_turn_claims
                SET StateCode = 5, TerminalAtUtc = $now
                WHERE ClaimId = $claim AND StateCode = 3;
                """;

            Bind(command, "$now", Iso(DateTimeOffset.UtcNow));

            Bind(command, "$claim", existing.Claim.ClaimId);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {

                await owner.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return new Error(
                    ErrorCodes.Covenant.StaleSnapshot,
                    "This session turn claim left its committed state before the erasure tombstone could apply.");

            }

        }

        return await CommitAndReadAsync(transaction, owner, existing.Claim.ClaimId, cancellationToken)
            .ConfigureAwait(false);

    }

    private static Error? RefuseBegin(
        SessionTurnClaimLease lease,
        AssistantReplyBeginReceipt begin,
        ClaimRow existing,
        Guid reservedAssistantEntryId)
    {

        if (existing.Claim.IsTerminal)
        {

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This session turn claim is terminal and cannot record an assistant begin.");

        }

        if (existing.ExecutorId != lease.ExecutorId || existing.Claim.OwnerBootId != lease.OwnerBootId)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "This session turn claim was taken over by another executor.");

        }

        if (begin.SessionId != existing.Claim.SessionId)
        {

            return new Error(
                ErrorCodes.Security.IdempotencyConflict,
                "This assistant begin names a different Session than the claim is bound to.");

        }

        // The reserved identity is the whole point of reserving one. Recording any other assistant
        // Entry would bind the claim to a placeholder its finalization guard does not cover.
        if (begin.AssistantEntryId != reservedAssistantEntryId)
        {

            return new Error(
                ErrorCodes.Security.IdempotencyConflict,
                "This assistant begin names an Entry identity other than the one the claim reserved.");

        }

        if (begin.Preflight.PreRequestHistoryRevision != existing.Claim.PreRequestHistoryRevision)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "Session history moved after this turn was admitted, so its frozen evidence no longer holds.");

        }

        return existing.Claim.State == SessionTurnClaimState.Begun
            && existing.Claim.UserEntryId != begin.UserEntryId
                ? new Error(
                    ErrorCodes.Security.IdempotencyConflict,
                    "This session turn claim already recorded a different user Entry.")
                : null;

    }

    /// <summary>
    /// Refuses a terminal state the claim's current live state cannot reach.
    /// </summary>
    /// <remarks>
    /// A pending claim has written no Entries, so it can only be abandoned; a committed answer would
    /// name a reply that does not exist. Erased is unreachable from any live state because it is a
    /// tombstone over a committed answer, not an outcome a turn can produce.
    /// </remarks>
    private static Error? RefuseLiveEdge(SessionTurnClaimState current, SessionTurnClaimState target) =>
        (current, target) switch
        {
            (SessionTurnClaimState.PendingMaintenance,
                SessionTurnClaimState.Discarded or SessionTurnClaimState.RestoredInterrupted) => null,

            (SessionTurnClaimState.Begun,
                SessionTurnClaimState.Committed
                or SessionTurnClaimState.Discarded
                or SessionTurnClaimState.RestoredInterrupted) => null,

            _ => new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                $"A {current} session turn claim cannot terminalize as {target}."),
        };

    private static Error? Validate(SessionTurnRequestIdentity request)
    {

        if (request.OriginInstallationId == Guid.Empty
            || request.ClientTurnId == Guid.Empty
            || request.SessionId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Validation.InvalidFields,
                "A session turn claim requires a nonempty installation, client turn, and Session identity.");

        }

        if (!Enum.IsDefined(request.Surface))
        {

            return new Error(
                ErrorCodes.Validation.InvalidFields,
                "A session turn claim requires a defined turn surface.");

        }

        if (!request.RequestDigest.IsValid || !request.DependencyDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Validation.InvalidFields,
                "A session turn claim requires both a request digest and an execution-dependency digest.");

        }

        return request.OriginRestoreEpoch < 0
            || request.PreRequestHistoryRevision < 0
            || request.InputSensitivityRevision < 0
                ? new Error(
                    ErrorCodes.Validation.InvalidFields,
                    "A session turn claim cannot freeze a negative restore epoch or revision.")
                : null;

    }

    private async ValueTask<Result> InsertClaimAsync(
        CovenantMutationTransaction transaction,
        SessionTurnRequestIdentity request,
        Guid claimId,
        Guid reservationId,
        Guid executorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            INSERT INTO session_turn_claims (
                ClaimId, OriginInstallationId, OriginRestoreEpoch, ClientTurnId, SessionId, SurfaceCode,
                RequestDigest, DependencyDigest, StateCode, PreRequestHistoryWatermarkUtc,
                PreRequestHistoryRevision, InputSensitivityRevision, ExpectedCurrentSensitivityRevision,
                FinalizationReservationId, UserEntryId, AssistantEntryId, OwnerBootId, ExecutorId,
                LeaseDeadlineUtc, HeartbeatAtUtc, CheckpointRevision, CompletedStepMask,
                TerminalErrorCode, TerminalHttpStatus, TerminalParameterBytes, TerminalParameterDigest,
                TerminalAtUtc, CreatedAtUtc)
            VALUES (
                $claim, $installation, $epoch, $client, $session, $surface,
                $request, $dependency, 1, $watermark,
                $history, $sensitivity, $sensitivity,
                $reservation, NULL, NULL, $boot, $executor,
                $deadline, $now, 0, 0,
                NULL, NULL, NULL, NULL,
                NULL, $now);
            """;

        Bind(command, "$claim", claimId);

        Bind(command, "$installation", request.OriginInstallationId);

        Bind(command, "$epoch", request.OriginRestoreEpoch);

        Bind(command, "$client", request.ClientTurnId);

        Bind(command, "$session", request.SessionId);

        Bind(command, "$surface", (int)request.Surface);

        Bind(command, "$request", request.RequestDigest.Bytes);

        Bind(command, "$dependency", request.DependencyDigest.Bytes);

        Bind(
            command,
            "$watermark",
            request.PreRequestHistoryWatermarkUtc is { } watermark ? Iso(watermark) : DBNull.Value);

        Bind(command, "$history", request.PreRequestHistoryRevision);

        Bind(command, "$sensitivity", request.InputSensitivityRevision);

        Bind(command, "$reservation", reservationId);

        Bind(command, "$boot", bootId);

        Bind(command, "$executor", executorId);

        Bind(command, "$deadline", Iso(now + LeaseDuration));

        Bind(command, "$now", Iso(now));

        try
        {

            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();

        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {

            // A constraint refusal here is the claim tier's own guard, so it is reported rather than
            // swallowed: the message names which invariant declined the row.
            return new Error(ErrorCodes.Covenant.LifecycleConflict, exception.Message);

        }

    }

    private static async Task<Result<SessionTurnClaim>> CommitAndReadAsync(
        CovenantMutationTransaction transaction,
        SqliteTransaction owner,
        Guid claimId,
        CancellationToken cancellationToken)
    {

        ClaimRow? updated = await ReadByClaimIdAsync(transaction, claimId, cancellationToken)
            .ConfigureAwait(false);

        if (updated is not { } row)
        {

            await owner.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The updated session turn claim could not be read back inside its own transaction.");

        }

        await owner.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result<SessionTurnClaim>.Success(row.Claim);

    }

    private static async ValueTask<bool> HasLiveClaimAsync(
        CovenantMutationTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT 1 FROM session_turn_claims
            WHERE SessionId = $session AND StateCode IN (1, 2)
            LIMIT 1;
            """;

        Bind(command, "$session", sessionId);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not (null or DBNull);

    }

    /// <summary>
    /// Reads the future assistant Entry identity from the capacity the claim reserved.
    /// </summary>
    /// <remarks>
    /// The identity lives on the reservation rather than on the claim because
    /// <c>session_turn_claims.AssistantEntryId</c> is null until the placeholder exists. Reading it
    /// back is what makes a resume, adoption, or replay reuse the one reserved identity instead of
    /// generating a second one.
    /// </remarks>
    private static async ValueTask<Guid> ReadReservedAssistantEntryAsync(
        CovenantMutationTransaction transaction,
        Guid reservationId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT AssistantEntryId FROM assistant_finalization_capacity_reservations
            WHERE ReservationId = $reservation;
            """;

        Bind(command, "$reservation", reservationId);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string text
            ? Guid.Parse(text, CultureInfo.InvariantCulture)
            : Guid.Empty;

    }

    private static ValueTask<ClaimRow?> ReadByClaimIdAsync(
        CovenantMutationTransaction transaction,
        Guid claimId,
        CancellationToken cancellationToken) =>
        ReadOneAsync(
            transaction,
            "WHERE ClaimId = $claim",
            command => Bind(command, "$claim", claimId),
            cancellationToken);

    private static ValueTask<ClaimRow?> ReadByClientIdentityAsync(
        CovenantMutationTransaction transaction,
        Guid originInstallationId,
        Guid clientTurnId,
        CancellationToken cancellationToken) =>
        ReadOneAsync(
            transaction,
            "WHERE OriginInstallationId = $installation AND ClientTurnId = $client",
            command =>
            {
                Bind(command, "$installation", originInstallationId);

                Bind(command, "$client", clientTurnId);
            },
            cancellationToken);

    private static async ValueTask<ClaimRow?> ReadOneAsync(
        CovenantMutationTransaction transaction,
        string predicate,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = $"SELECT {ClaimColumns} FROM session_turn_claims {predicate};";

        bind(command);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Materialize(reader)
            : null;

    }

    private static ClaimRow Materialize(SqliteDataReader reader)
    {

        SessionTurnClaimState state = (SessionTurnClaimState)reader.GetInt32(8);

        SessionTurnClaim claim = new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            reader.GetInt64(2),
            ReadGuid(reader, 3),
            ReadGuid(reader, 4),
            (SessionTurnSurface)reader.GetInt32(5),
            new CovenantDigest((byte[])reader.GetValue(6)),
            new CovenantDigest((byte[])reader.GetValue(7)),
            state,
            ReadOptionalTimestamp(reader, 9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            ReadGuid(reader, 13),
            ReadOptionalGuid(reader, 14),
            ReadOptionalGuid(reader, 15),
            ReadOptionalGuid(reader, 16),
            reader.GetInt64(19),
            reader.GetInt32(20),
            ReadOutcome(reader, state),
            ReadOptionalTimestamp(reader, 24) ?? default);

        return new ClaimRow(claim, ReadOptionalGuid(reader, 17), ReadOptionalTimestamp(reader, 18));

    }

    private static SessionTurnClaimOutcome? ReadOutcome(SqliteDataReader reader, SessionTurnClaimState state) =>
        state switch
        {
            SessionTurnClaimState.Committed => SessionTurnClaimOutcome.Committed(),

            SessionTurnClaimState.Erased => SessionTurnClaimOutcome.Erased(),

            SessionTurnClaimState.Discarded => SessionTurnClaimOutcome.Discarded(
                reader.GetString(21),
                reader.GetInt32(22),
                [.. (byte[])reader.GetValue(23)]),

            SessionTurnClaimState.RestoredInterrupted => SessionTurnClaimOutcome.RestoredInterrupted(
                reader.GetString(21),
                reader.GetInt32(22),
                [.. (byte[])reader.GetValue(23)]),

            _ => null,
        };

    private static void BindOutcome(SqliteCommand command, SessionTurnClaimOutcome outcome)
    {

        Bind(command, "$state", (int)outcome.State);

        Bind(command, "$code", outcome.TerminalErrorCode ?? (object)DBNull.Value);

        Bind(command, "$status", outcome.TerminalHttpStatus is { } status ? status : DBNull.Value);

        ImmutableArray<byte> parameters = outcome.TerminalParameterBytes;

        Bind(command, "$parameters", parameters.IsDefault ? DBNull.Value : (object)parameters.ToArray());

        Bind(
            command,
            "$digest",
            outcome.TerminalParameterDigest is { } digest ? digest.Bytes : (object)DBNull.Value);

    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal) =>
        Guid.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static Guid? ReadOptionalGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static DateTimeOffset? ReadOptionalTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(
                reader.GetString(ordinal),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// Binds a value, with identities bound as <see cref="Guid"/> rather than formatted text.
    /// </summary>
    /// <remarks>
    /// The claim tier carries real foreign keys to EF-owned <c>Sessions</c> rows and its insert
    /// trigger compares identities against the reservation table. EF's SQLite mapping writes an
    /// uppercase <c>D</c>-format literal, so a lowercase parameter would match nothing and every claim
    /// would fail its foreign key in a real host while passing against a suite that seeded its own
    /// lowercase rows.
    /// </remarks>
    private static void Bind(SqliteCommand command, string name, object value) =>
        _ = command.Parameters.AddWithValue(name, value);

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private readonly record struct ClaimRow(
        SessionTurnClaim Claim,
        Guid? ExecutorId,
        DateTimeOffset? LeaseDeadlineUtc);

}
