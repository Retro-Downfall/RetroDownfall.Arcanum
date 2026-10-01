using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>A prepared Covenant erase and the installation read lease its protected response holds.</summary>
/// <remarks>
/// The lease is typed as the interface so the route can hand it to its protected response, which
/// revalidates it before the first byte and releases it once the body is written.
/// </remarks>
internal sealed record CovenantEntryErasurePrepared(MemoryErasurePreflightDto Preflight, ICovenantSnapshotReadLease ReadLease);

/// <summary>Prepares a Covenant erase and hands its read lease to the protected response that writes it.</summary>
internal interface ICovenantEntryErasurePreparer
{
    /// <summary>
    /// Measures one Covenant entry erasure under an installation read lease, and returns the lease still
    /// held, or a refusal with no lease held.
    /// </summary>
    Task<Result<CovenantEntryErasurePrepared>> PrepareHeldAsync(
        CovenantErasePrepareRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken);
}

/// <summary>
/// Selective hard erasure of one whole Covenant entry: both lanes, every version, its receipts, its
/// pending outbox deltas, its search documents and its curation, with the key reclaimed when no other
/// entry names it.
/// </summary>
/// <remarks>
/// <para><b>Prepare.</b> The canonical tier must record version 6, which is the first that can record an
/// entry erasure, before the key is opened, so a catalog that cannot erase creates no key. The key is
/// opened, and on an installation with no evidence created, before any lease or transaction, because
/// key access can read the OS credential store. Only then is the installation read lease taken, and one
/// read snapshot on its admitted connection compares the entry and both heads with what show reported
/// and measures the plan, the scope facts and the exposure with raw SQL rather than through a
/// lease-validated store port. The token is an operator preflight envelope over a
/// <see cref="CovenantErasurePreflightBody"/>, which binds the request and effect digests, the
/// operator's authority epoch, the dataset generation and both key epochs, and whether the key is
/// reclaimed.</para>
///
/// <para><b>Apply.</b> A committed erase answers by receipt before any token is read, under a short read
/// lease over the entry's scope that is released before any closure is asked for, so a replay never
/// closes a scope and the drain never waits on the erase's own lease. An entry already gone takes no
/// closure at all: one transaction with no erasure authority answers it as a replay, a 410 or a 404.
/// Otherwise the token must bind this exact request, and the entry-erasure closure is taken over the
/// entry's Campaign, or over the installation for a Global entry or a reclaiming erase, and drained. The
/// lease is revalidated straight away, because it records the authority from before the drain. The
/// erase is then one <c>BEGIN IMMEDIATE</c> under entry-erasure authorization that re-proves the receipt,
/// the subject, the dataset and both key epochs, both heads and the Campaign, refuses a reclamation the
/// closure does not cover, recomputes the effect, deletes through the plan, records the fingerprint and
/// the receipt, proves absence, revalidates the lease and commits.</para>
///
/// <para><b>The one disposition.</b> Every completion passes <see cref="CancellationToken.None"/>: a
/// cancelled completion claims the lease's one disposition and then throws, which would leave the scope
/// closed until the host restarts. A committed erase reopens as a commit and a proven refusal as a
/// rollback. A <c>COMMIT</c> that fails is settled by reading the receipt back on a fresh connection
/// after its transaction is disposed: a receipt there is the committed erase; none means nothing
/// changed, a rollback, and a retryable refusal. The scope stays closed, and the erase asks for manual
/// recovery, whenever that is not proved: a read that cannot be made or throws, or a rollback of the
/// failed transaction that itself fails. The erase publishes no availability, generation or authority transition,
/// so a commit needs no health publication before it reopens.</para>
///
/// <para>The result is built from the receipt only after a commit that succeeded or whose receipt a fresh
/// connection read back, and only after the closure has reopened, so the write-ahead-log scrub never
/// runs while the scope is closed. Nothing here logs, and errors name no content.</para>
/// </remarks>
internal sealed class CovenantEntryErasureService(
    ICovenantConnectionSource connections,
    IMemoryErasureKeyCreator keyCreator,
    IMemoryErasureKeyProvider keys,
    ICovenantEnvelopeCodec envelopes,
    MemoryErasureScrubber scrubber,
    ICovenantOperationGate gate,
    ICovenantSqliteConnectionInitializer initializer,
    IOptionsMonitor<ArcanumSettings> options,
    TimeProvider timeProvider) : ICovenantEntryErasureService, ICovenantEntryErasurePreparer
{
    private const int ScrubPending = 1;

    /// <summary>The first canonical schema version whose delete guards admit an entry erasure.</summary>
    private const long MinimumCanonicalSchemaVersion = 6;

    private static readonly Error InvalidBody = new(
        ErrorCodes.Validation.InvalidBody,
        "A Covenant erase names a scope, a Campaign exactly when it is Campaign scope, a key, the entry id and lane heads show reported, a mutation id and, to apply, a preflight token.");

    private static readonly Error InvalidScope = new(
        ErrorCodes.Covenant.InvalidScope,
        "A Global Covenant entry has no Proposed lane, and a Campaign scope names exactly one Campaign.");

    private static readonly Error InvalidKey = new(
        ErrorCodes.Covenant.InvalidKey,
        "A Covenant key must match [a-z0-9][a-z0-9._-]{0,127}.");

    private static readonly Error NotFound = new(
        ErrorCodes.Covenant.NotFound,
        "No Covenant entry exists with that id.");

    private static readonly Error SubjectErased = new(
        ErrorCodes.MemoryErasure.SubjectErased,
        "This Covenant entry was already erased.");

    private static readonly Error RevisionConflict = new(
        ErrorCodes.Covenant.RevisionConflict,
        "The entry or one of its lane heads is not the one this erase expected; show the key again before erasing it.");

    private static readonly Error StalePlan = new(
        ErrorCodes.MemoryErasure.StalePlan,
        "What this erase would remove changed after it was prepared; prepare it again.");

    private static readonly Error AuthorityChanged = new(
        ErrorCodes.Covenant.StaleSnapshot,
        "Operator authority changed after this erase was prepared.");

    private static readonly Error CampaignGone = new(
        ErrorCodes.Covenant.StaleSnapshot,
        "The entry's Campaign is no longer registered, so its entry is left to owner cleanup.");

    private static readonly Error UncoveredReclamation = new(
        ErrorCodes.Covenant.ForbiddenAuthority,
        "Erasing a Global entry or reclaiming a Covenant key requires a closure over the whole installation.");

    private static readonly Error NotAbsent = new(
        ErrorCodes.MemoryErasure.ErasureIncomplete,
        "The erase could not prove every planned row absent, so it rolled back and recorded nothing.");

    private static readonly Error CommitNotRecorded = new(
        ErrorCodes.Covenant.MaintenanceFailed,
        "The erase's commit failed and recorded nothing, so nothing changed; retry it.");

    private static readonly Error CommitUnsettled = new(
        ErrorCodes.Covenant.ManualRecoveryRequired,
        "The erase's commit failed and its outcome could not be read back, so its Covenant scope stays closed until the host restarts.");

    private static readonly Error CanonicalUnavailable = new(
        ErrorCodes.MemoryErasure.Unavailable,
        "Covenant entry erasure is unavailable until the Covenant canonical schema reaches the version that records erasures; retry shortly.");

    /// <summary>Test seam: replaces the transaction's <c>COMMIT</c> when set.</summary>
    internal Func<SqliteTransaction, CancellationToken, Task>? CommitForTesting { get; init; }

    /// <summary>Test seam: replaces the fresh-connection receipt re-read of an uncertain commit when set.</summary>
    internal Func<Guid, CancellationToken, Task<Result<MemoryErasureReceiptRow?>>>? ReceiptReReadForTesting { get; init; }

    /// <summary>
    /// Test seam: runs once the pre-transaction receipt probe has found no receipt, while the probe's
    /// short read lease is still held. Never set in production.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterReceiptProbeForTesting { get; init; }

    public async Task<Result<MemoryErasurePreflightDto>> PrepareAsync(
        CovenantErasePrepareRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        Result<CovenantEntryErasurePrepared> prepared = await PrepareHeldAsync(request, authority, cancellationToken)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {
            return prepared.Error;
        }

        await prepared.Value.ReadLease.DisposeAsync().ConfigureAwait(false);

        return prepared.Value.Preflight;
    }

    public async Task<Result<CovenantEntryErasurePrepared>> PrepareHeldAsync(
        CovenantErasePrepareRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);

        Result<Target> parsed = Parse(request);

        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        Target target = parsed.Value;

        SqliteConnection core = await connections.GetOpenCoreConnectionAsync(cancellationToken).ConfigureAwait(false);

        Result ready = await RequireReadyAsync(core, cancellationToken).ConfigureAwait(false);

        if (ready.IsFailure)
        {
            return ready.Error;
        }

        // Before any lease or transaction: opening the key can read, and on a fresh installation write,
        // the OS credential store.
        Result<MemoryErasureKey> opened = await MemoryErasureProtocol
            .OpenKeyForPrepareAsync(core, keyCreator, MemoryReviewStore.Covenant, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        using MemoryErasureKey key = opened.Value;

        Result<CovenantInstallationReadLease> acquired = await gate.AcquireInstallationReadAsync(cancellationToken).ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return acquired.Error;
        }

        CovenantInstallationReadLease? owned = acquired.Value;

        try
        {
            SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            Result<Measurement> measured = await SqliteBusyRetry.ExecuteAsync<Result<Measurement>>(
                async () =>
                {
                    await using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);

                    Result<Located> located = await LocateAsync(connection, snapshot, key, target, cancellationToken).ConfigureAwait(false);

                    if (located.IsFailure)
                    {
                        return located.Error;
                    }

                    if (located.Value.Subject.CampaignId is { } campaign
                        && !await CovenantEntryErasurePlan.CampaignIsLiveAsync(connection, snapshot, campaign, cancellationToken).ConfigureAwait(false))
                    {
                        return CampaignGone;
                    }

                    CovenantEntryErasureState state = await CovenantEntryErasurePlan
                        .ReadStateAsync(connection, snapshot, target.Key, cancellationToken)
                        .ConfigureAwait(false);

                    return await MeasureAsync(connection, snapshot, located.Value, state, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (measured.IsFailure)
            {
                return measured.Error;
            }

            Measurement plan = measured.Value;

            byte[] requestDigest = key.CovenantRequest(target.MutationId, target.Request);

            byte[] effectDigest = key.Effect(plan.Facts);

            DateTimeOffset issuedAt = timeProvider.GetUtcNow();

            long issuedMilliseconds = issuedAt.ToUnixTimeMilliseconds();

            long expiresMilliseconds = (issuedAt + MemoryReviewLimits.TokenLifetime).ToUnixTimeMilliseconds();

            CovenantErasurePreflightBody body = new(
                new CovenantDigest(requestDigest),
                checked((ulong)authority.AuthorityEpoch),
                plan.State.DatasetGeneration,
                checked((ulong)plan.State.KeyEpoch),
                checked((ulong)plan.State.KeyReclamationEpoch),
                plan.Located.Subject.ReclaimsKey,
                target.EntryId,
                target.Request.Confirmed?.VersionId,
                target.Request.Proposed?.VersionId,
                new CovenantDigest(effectDigest),
                issuedMilliseconds,
                expiresMilliseconds);

            // The body repeats these instants and apply requires the two to agree, so the instant is
            // stated rather than read a second time inside the codec.
            Result<string> token = envelopes.Encode(
                CovenantEnvelopePurpose.OperatorPreflight,
                body.Encode(),
                MemoryReviewLimits.TokenLifetime,
                issuedAt);

            if (token.IsFailure)
            {
                return token.Error;
            }

            MemoryErasurePreflightDto preflight = new(
                MemoryReviewStore.Covenant,
                target.MutationId,
                Convert.ToHexStringLower(requestDigest),
                Convert.ToHexStringLower(effectDigest),
                new MemoryErasurePlanDto(
                    1,
                    plan.Measured.Targets.Sum(static table => table.Rows),
                    0,
                    0,
                    plan.ScopeFacts.IsPinned,
                    Lexicon: null,
                    CovenantEntryErasurePlan.Facts(plan.Located.Subject, plan.Measured, plan.ScopeFacts)),
                plan.Exposure,
                plan.Retained,
                MemoryErasureNotes.For(MemoryReviewStore.Covenant, target.Identity.Scope, plan.Located.Subject.ReclaimsKey),
                DateTimeOffset.FromUnixTimeMilliseconds(issuedMilliseconds),
                DateTimeOffset.FromUnixTimeMilliseconds(expiresMilliseconds),
                token.Value);

            CovenantEntryErasurePrepared prepared = new(preflight, owned);

            // Ownership moves to the caller, who hands the lease to the protected response.
            owned = null;

            return prepared;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<Result<MemoryErasureResultDto>> ApplyAsync(
        CovenantEraseRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);

        if (request is null || string.IsNullOrWhiteSpace(request.PreflightToken))
        {
            return InvalidBody;
        }

        Result<Target> parsed = Parse(request.ToPrepareRequest());

        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        Target target = parsed.Value;

        SqliteConnection core = await connections.GetOpenCoreConnectionAsync(cancellationToken).ConfigureAwait(false);

        Result ready = await RequireReadyAsync(core, cancellationToken).ConfigureAwait(false);

        if (ready.IsFailure)
        {
            return ready.Error;
        }

        Result<MemoryErasureKey> opened = await MemoryErasureProtocol
            .OpenKeyForApplyAsync(core, keys, MemoryReviewStore.Covenant, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        using MemoryErasureKey key = opened.Value;

        // From the request alone, so a committed erase can answer before any token is read.
        byte[] requestDigest = key.CovenantRequest(target.MutationId, target.Request);

        // A replay cannot tell from its receipt whether the key was reclaimed, and a reclaimed key's
        // curation went in every scope, so a replay never claims that other scopes were unaffected.
        MemoryErasureNote[] replayNotes = MemoryErasureNotes.For(MemoryReviewStore.Covenant, target.Identity.Scope, reclaimsKey: true);

        Result<CovenantReadLease> probe = await gate.AcquireReadAsync(target.Scope, cancellationToken).ConfigureAwait(false);

        if (probe.IsFailure)
        {
            return probe.Error;
        }

        SqliteConnection connection;

        Result<MemoryErasureReceiptRow?> recorded;

        bool present = false;

        // Released before any closure is asked for: a probe lease still held would be drained against
        // itself.
        await using (probe.Value)
        {
            connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            recorded = await MemoryErasureProtocol
                .ProbeReceiptAsync(connection, null, MemoryReviewStore.Covenant, target.MutationId, requestDigest, cancellationToken)
                .ConfigureAwait(false);

            if (recorded.IsSuccess && recorded.Value is null)
            {
                if (AfterReceiptProbeForTesting is { } afterProbe)
                {
                    await afterProbe(cancellationToken).ConfigureAwait(false);
                }

                present = await EntryPresentAsync(connection, target.EntryId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value is { } committed)
        {
            return await MemoryErasureProtocol
                .FinishAsync(connection, scrubber, committed, replayed: true, replayNotes, CancellationToken.None)
                .ConfigureAwait(false);
        }

        Result<CovenantErasurePreflightBody> read = ReadToken(request.PreflightToken, requestDigest, target);

        if (read.IsFailure)
        {
            return read.Error;
        }

        CovenantErasurePreflightBody body = read.Value;

        if (body.OperatorAuthorityEpoch != checked((ulong)authority.AuthorityEpoch))
        {
            return AuthorityChanged;
        }

        Result<Applied> applied;

        if (!present)
        {
            // The entry is gone, so there is nothing to close a scope for: the transaction alone answers,
            // replaying a receipt that committed meanwhile, refusing an erased subject with 410 and a
            // missing entry with 404.
            applied = await SqliteBusyRetry.ExecuteAsync(
                () => AnswerAbsentAsync(connection, key, target, requestDigest, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Result<CovenantEntryErasureLease> closed = await gate
                .AcquireEntryErasureAsync(
                    target.Scope,
                    body.ReclaimsKey,
                    new CovenantExclusiveRecoveryOwner(target.MutationId, CovenantExclusiveOperation.CovenantEntryErasure, body.EffectDigest),
                    cancellationToken)
                .ConfigureAwait(false);

            if (closed.IsFailure)
            {
                return closed.Error;
            }

            applied = await ApplyUnderClosureAsync(connection, closed.Value, key, target, requestDigest, body, authority, cancellationToken)
                .ConfigureAwait(false);
        }

        if (applied.IsFailure)
        {
            return applied.Error;
        }

        // A commit of this request proved, inside its transaction, that the subject's reclamation is the
        // one the token recorded, so its notes say exactly that.
        MemoryErasureNote[] notes = applied.Value.Replayed
            ? replayNotes
            : MemoryErasureNotes.For(MemoryReviewStore.Covenant, target.Identity.Scope, body.ReclaimsKey);

        // Reached only after a commit that succeeded or whose receipt a fresh connection read back, or
        // for a receipt a transaction found already committed, and only once any closure has reopened.
        return await MemoryErasureProtocol
            .FinishAsync(connection, scrubber, applied.Value.Receipt, applied.Value.Replayed, notes, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the erase under its closure and completes the closure with the one disposition the outcome
    /// decides, always with <see cref="CancellationToken.None"/>.
    /// </summary>
    private async Task<Result<Applied>> ApplyUnderClosureAsync(
        SqliteConnection connection,
        CovenantEntryErasureLease lease,
        MemoryErasureKey key,
        Target target,
        byte[] requestDigest,
        CovenantErasurePreflightBody body,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveLeaseDisposition.RollbackAndReopen;

        Result<Applied> applied;

        try
        {
            // The lease records the authority from before the drain, so only a revalidation sees an
            // authority change made while it drained.
            Result current = await lease.RevalidateAsync(CancellationToken.None).ConfigureAwait(false);

            if (current.IsFailure
                || lease.Snapshot.RuntimeAuthorityGeneration != authority.RuntimeAuthorityGeneration
                || lease.Snapshot.AuthorityEpoch != authority.AuthorityEpoch)
            {
                return AuthorityChanged;
            }

            if (lease.Snapshot.DatasetGeneration != body.DatasetGeneration)
            {
                return StalePlan;
            }

            try
            {
                applied = await SqliteBusyRetry.ExecuteAsync(
                    () => ApplyInTransactionAsync(connection, lease, key, target, requestDigest, body, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (applied.IsSuccess)
                {
                    disposition = CovenantExclusiveLeaseDisposition.CommitAndReopen;
                }
            }
            catch (UncertainCommitException uncertain)
            {
                // Closed until the outcome is proved: only a re-read that answers reopens the scope,
                // whatever else fails on the way.
                disposition = CovenantExclusiveLeaseDisposition.KeepClosed;

                applied = CommitUnsettled;

                // A rollback that failed proves nothing about the commit, and may have left the
                // connection's own transaction in an unknown state, so nothing is read through it.
                Result<MemoryErasureReceiptRow?> reread = uncertain.RollbackFailed
                    ? Result<MemoryErasureReceiptRow?>.Failure(CommitUnsettled)
                    : await ReReadReceiptAsync(target.MutationId).ConfigureAwait(false);

                if (reread.IsFailure)
                {
                    applied = CommitUnsettled;
                }
                else if (reread.Value is { Store: MemoryReviewStore.Covenant } persisted
                    && CryptographicOperations.FixedTimeEquals(persisted.RequestDigest, requestDigest))
                {
                    disposition = CovenantExclusiveLeaseDisposition.CommitAndReopen;

                    applied = new Applied(persisted, Replayed: false);
                }
                else
                {
                    disposition = CovenantExclusiveLeaseDisposition.RollbackAndReopen;

                    applied = CommitNotRecorded;
                }
            }
        }
        finally
        {
            // A completion that fails leaves nothing to report differently: a committed erase is still
            // the committed erase, and a refusal is still the refusal.
            _ = await lease.CompleteAsync(disposition, CancellationToken.None).ConfigureAwait(false);

            await lease.DisposeAsync().ConfigureAwait(false);
        }

        return applied;
    }

    /// <summary>
    /// Reads the receipt back on a connection of its own, the transaction that may have committed it
    /// already disposed. Any exception is a read that could not be made, never a proof either way.
    /// </summary>
    private async Task<Result<MemoryErasureReceiptRow?>> ReReadReceiptAsync(Guid mutationId)
    {
        try
        {
            return ReceiptReReadForTesting is { } seam
                ? await seam(mutationId, CancellationToken.None).ConfigureAwait(false)
                : await scrubber.ReadCommittedReceiptAsync(mutationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Result<MemoryErasureReceiptRow?>.Failure(CommitUnsettled);
        }
    }

    /// <summary>
    /// The erase itself: one <c>BEGIN IMMEDIATE</c> under entry-erasure authorization that re-proves
    /// everything the plan assumed, removes the entry, records the evidence, proves absence, and commits.
    /// </summary>
    private async Task<Result<Applied>> ApplyInTransactionAsync(
        SqliteConnection connection,
        CovenantEntryErasureLease lease,
        MemoryErasureKey key,
        Target target,
        byte[] requestDigest,
        CovenantErasurePreflightBody body,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        using CovenantSqliteAuthorizationScope authorized = initializer.Authorize(
            connection,
            CovenantSqliteAuthorizationKind.CovenantEntryErasure);

        // A concurrent identical apply may have committed while this one waited for the write lock.
        Result<MemoryErasureReceiptRow?> recorded = await MemoryErasureProtocol
            .ProbeReceiptAsync(connection, transaction, MemoryReviewStore.Covenant, target.MutationId, requestDigest, cancellationToken)
            .ConfigureAwait(false);

        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value is { } committed)
        {
            return new Applied(committed, Replayed: true);
        }

        if (await MemoryErasureEvidence
                .SubjectErasedAsync(connection, transaction, key.Subject(MemoryReviewStore.Covenant, target.EntryId.ToString()), cancellationToken)
                .ConfigureAwait(false))
        {
            return SubjectErased;
        }

        CovenantEntryErasureState state = await CovenantEntryErasurePlan
            .ReadStateAsync(connection, transaction, target.Key, cancellationToken)
            .ConfigureAwait(false);

        if (state.DatasetGeneration != body.DatasetGeneration
            || checked((ulong)state.KeyReclamationEpoch) != body.KeyReclamationEpoch)
        {
            return StalePlan;
        }

        Result<Located> located = await LocateAsync(connection, transaction, key, target, cancellationToken).ConfigureAwait(false);

        if (located.IsFailure)
        {
            return located.Error;
        }

        if (checked((ulong)state.KeyEpoch) != body.KeyEpoch)
        {
            return StalePlan;
        }

        CovenantEntryErasureSubject subject = located.Value.Subject;

        if (subject.CampaignId is { } campaign
            && !await CovenantEntryErasurePlan.CampaignIsLiveAsync(connection, transaction, campaign, cancellationToken).ConfigureAwait(false))
        {
            return CampaignGone;
        }

        if (subject.ReclaimsKey != body.ReclaimsKey)
        {
            return StalePlan;
        }

        // Every Campaign's turns read a Global entry, and reclamation removes the key's curation in
        // every scope, so either one needs a closure that drained the whole installation.
        if ((subject.ReclaimsKey || subject.Scope is CovenantScope.Global) && !lease.CoversInstallation)
        {
            return UncoveredReclamation;
        }

        Measurement erasing = await MeasureAsync(connection, transaction, located.Value, state, cancellationToken).ConfigureAwait(false);

        byte[] effectDigest = key.Effect(erasing.Facts);

        if (!CryptographicOperations.FixedTimeEquals(effectDigest, body.EffectDigest.Bytes))
        {
            return StalePlan;
        }

        // The rows removed are the rows just measured, which the absence proof below shows are gone, so
        // the count includes the review events and decision receipts the versions' delete cascades.
        long removedRows = erasing.Measured.Targets.Sum(static table => table.Rows);

        CovenantEntryErasureTally removed = await CovenantEntryErasurePlan
            .RunAsync(connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, cancellationToken)
            .ConfigureAwait(false);

        _ = await MemoryErasureEvidence
            .InsertFingerprintAsync(
                connection,
                transaction,
                key.Fingerprint(target.Identity),
                MemoryReviewStore.Covenant,
                key.KeyId.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<MemoryExternalEvidence> evidence = erasing.Facts.Evidence;

        MemoryErasureReceiptRow receipt = new(
            target.MutationId,
            MemoryReviewStore.Covenant,
            key.KeyId.ToArray(),
            requestDigest,
            effectDigest,
            1,
            removedRows,
            0,
            0,
            evidence[0],
            evidence[1],
            evidence[2],
            evidence[3],
            evidence[4],
            erasing.Facts.RetainedCopiesMask,
            ScrubPending,
            MemoryErasureScrubPendingReasons.ToMask(
                removed.FullTextSecureDeleteVerified
                    ? [MemoryErasureScrubPendingReason.WalCheckpointPending]
                    : [MemoryErasureScrubPendingReason.WalCheckpointPending, MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified]));

        await MemoryErasureEvidence
            .InsertReceiptAsync(
                connection,
                transaction,
                receipt,
                [key.Subject(MemoryReviewStore.Covenant, target.EntryId.ToString())],
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<MemoryErasureTableCount> remaining = await CovenantEntryErasurePlan
            .ProveAbsentAsync(connection, transaction, subject, removed.VersionIds, cancellationToken)
            .ConfigureAwait(false);

        if (remaining.Count != 0)
        {
            return NotAbsent;
        }

        Result revalidated = await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {
            return revalidated.Error;
        }

        try
        {
            await (CommitForTesting is { } commit
                ? commit(transaction, cancellationToken)
                : transaction.CommitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException && !IsBusy(failure))
        {
            // A busy COMMIT left the transaction open, and the retry's first step re-probes the receipt.
            // Anything else may have persisted the frame before it failed, so the outcome is uncertain.
            // The transaction is rolled back here, where a rollback that fails is recorded rather than
            // left to replace the commit's own failure on the way out of this method.
            bool rollbackFailed = false;

            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                rollbackFailed = true;
            }

            throw new UncertainCommitException(failure, rollbackFailed);
        }

        return new Applied(receipt, Replayed: false);
    }

    /// <summary>
    /// Answers an entry that was already gone before any closure was asked for, in one transaction that
    /// holds no erasure authority and so can delete nothing.
    /// </summary>
    private static async Task<Result<Applied>> AnswerAbsentAsync(
        SqliteConnection connection,
        MemoryErasureKey key,
        Target target,
        byte[] requestDigest,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result<MemoryErasureReceiptRow?> recorded = await MemoryErasureProtocol
            .ProbeReceiptAsync(connection, transaction, MemoryReviewStore.Covenant, target.MutationId, requestDigest, cancellationToken)
            .ConfigureAwait(false);

        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value is { } committed)
        {
            return new Applied(committed, Replayed: true);
        }

        if (await MemoryErasureEvidence
                .SubjectErasedAsync(connection, transaction, key.Subject(MemoryReviewStore.Covenant, target.EntryId.ToString()), cancellationToken)
                .ConfigureAwait(false))
        {
            return SubjectErased;
        }

        // Entry ids are random and never reused, so an entry seen absent cannot return. One that somehow
        // did was not the entry this erase measured.
        return await CovenantEntryErasurePlan.ReadSubjectAsync(connection, transaction, target.EntryId, cancellationToken).ConfigureAwait(false) is null
            ? NotFound
            : StalePlan;
    }

    /// <summary>
    /// The entry and both of its heads, compared with what show reported: an absent entry is 410 when a
    /// receipt names it and 404 otherwise, and any other difference is a revision conflict.
    /// </summary>
    private static async Task<Result<Located>> LocateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryErasureKey key,
        Target target,
        CancellationToken cancellationToken)
    {
        CovenantEntryErasureSubject? subject = await CovenantEntryErasurePlan
            .ReadSubjectAsync(connection, transaction, target.EntryId, cancellationToken)
            .ConfigureAwait(false);

        if (subject is null)
        {
            return await MemoryErasureEvidence
                .SubjectErasedAsync(connection, transaction, key.Subject(MemoryReviewStore.Covenant, target.EntryId.ToString()), cancellationToken)
                .ConfigureAwait(false)
                ? SubjectErased
                : NotFound;
        }

        if (subject.Scope != target.Request.Scope
            || subject.CampaignId != target.Request.CampaignId
            || !string.Equals(subject.NormalizedKey, target.Key, StringComparison.Ordinal))
        {
            return RevisionConflict;
        }

        IReadOnlyDictionary<CovenantLane, CovenantEntryErasureHead> heads = await CovenantEntryErasurePlan
            .ReadHeadsAsync(connection, transaction, target.EntryId, cancellationToken)
            .ConfigureAwait(false);

        CovenantEntryErasureHead? confirmed = heads.GetValueOrDefault(CovenantLane.Confirmed);

        CovenantEntryErasureHead? proposed = heads.GetValueOrDefault(CovenantLane.Proposed);

        return HeadMatches(confirmed, target.Request.Confirmed) && HeadMatches(proposed, target.Request.Proposed)
            ? new Located(subject, confirmed, proposed)
            : RevisionConflict;
    }

    /// <summary>
    /// Measures everything the effect digest binds, in the caller's snapshot, with the plan's own Count.
    /// </summary>
    private async Task<Measurement> MeasureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Located located,
        CovenantEntryErasureState state,
        CancellationToken cancellationToken)
    {
        CovenantEntryErasureTally measured = await CovenantEntryErasurePlan
            .RunAsync(connection, transaction, located.Subject, CovenantArtifactPlanMode.Count, CovenantEntryErasureMode.Live, cancellationToken)
            .ConfigureAwait(false);

        CovenantEntryErasureScopeFacts scopeFacts = await CovenantEntryErasurePlan
            .ReadScopeFactsAsync(connection, transaction, located.Subject, cancellationToken)
            .ConfigureAwait(false);

        MemoryErasureExternalExposureDto exposure = await MemoryErasureExposure
            .ReadCovenantAsync(connection, transaction, located.Subject.EntryId, cancellationToken)
            .ConfigureAwait(false);

        MemoryRetainedLocalCopy[] retained = MemoryErasureRetainedCopies.For(
            MemoryReviewStore.Covenant,
            MemoryErasureRetainedCopies.AuditFilesExist(options.CurrentValue));

        MemoryErasureEffectFlags flags = MemoryErasureEffectFlags.None;

        if (scopeFacts.IsPinned)
        {
            flags |= MemoryErasureEffectFlags.Pinned;
        }

        if (located.Subject.ReclaimsKey)
        {
            flags |= MemoryErasureEffectFlags.ReclaimsKey;
        }

        if (measured.RetainsCampaignMask)
        {
            flags |= MemoryErasureEffectFlags.RetainsCampaignMask;
        }

        if (scopeFacts.GlobalConfirmedResurfaces)
        {
            flags |= MemoryErasureEffectFlags.GlobalConfirmedResurfaces;
        }

        MemoryErasureEffectFacts facts = new(
            MemoryReviewStore.Covenant,
            [located.Subject.EntryId.ToString()],
            [located.Confirmed?.VersionId],
            measured.Targets,
            0,
            0,
            flags,
            new CovenantErasureEffectFacts(state.DatasetGeneration, state.KeyEpoch, state.KeyReclamationEpoch),
            MemoryErasureExposure.EvidenceCodes(exposure),
            MemoryRetainedLocalCopies.ToMask(retained));

        return new Measurement(located, state, measured, scopeFacts, exposure, retained, facts);
    }

    /// <summary>
    /// Requires a catalog that can record an entry erasure: the Core evidence tables, and a canonical
    /// tier at version 6 or later.
    /// </summary>
    private static async Task<Result> RequireReadyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        Result installed = await MemoryErasureProtocol.RequireInstalledAsync(connection, cancellationToken).ConfigureAwait(false);

        if (installed.IsFailure)
        {
            return installed;
        }

        return await CovenantEntryErasurePlan.ReadCanonicalSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false)
            is >= MinimumCanonicalSchemaVersion
            ? Result.Success()
            : Result.Failure(CanonicalUnavailable);
    }

    /// <summary>Whether the entry still exists, read in a short snapshot of its own.</summary>
    private static async Task<bool> EntryPresentAsync(SqliteConnection connection, Guid entryId, CancellationToken cancellationToken) =>
        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                await using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);

                return await CovenantEntryErasurePlan.ReadSubjectAsync(connection, snapshot, entryId, cancellationToken).ConfigureAwait(false) is not null;
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the preflight token, which must be an operator preflight envelope over an erasure body that
    /// binds exactly this request, with the envelope's own instants.
    /// </summary>
    private Result<CovenantErasurePreflightBody> ReadToken(string token, byte[] requestDigest, Target target)
    {
        Result<CovenantEnvelopeBody> envelope = envelopes.Decode(CovenantEnvelopePurpose.OperatorPreflight, token);

        if (envelope.IsFailure)
        {
            return MemoryErasureProtocol.InvalidPreflightError;
        }

        Result<CovenantErasurePreflightBody> decoded = CovenantErasurePreflightBody.TryDecode(envelope.Value.Payload);

        if (decoded.IsFailure)
        {
            return MemoryErasureProtocol.InvalidPreflightError;
        }

        CovenantErasurePreflightBody body = decoded.Value;

        bool binds = CryptographicOperations.FixedTimeEquals(body.RequestDigest.Bytes, requestDigest)
            && body.IssuedAt == envelope.Value.IssuedAtUtc.ToUnixTimeMilliseconds()
            && body.ExpiresAt == envelope.Value.ExpiresAtUtc.ToUnixTimeMilliseconds()
            && body.EntryId == target.EntryId
            && body.ConfirmedVersionId == target.Request.Confirmed?.VersionId
            && body.ProposedVersionId == target.Request.Proposed?.VersionId;

        return binds ? body : MemoryErasureProtocol.InvalidPreflightError;
    }

    /// <summary>
    /// Refuses anything but a recognized scope with its Campaign exactly when it is Campaign scope, a
    /// valid key, a nonempty entry id, nonnegative head expectations, and a nonempty mutation id.
    /// </summary>
    private static Result<Target> Parse(CovenantErasePrepareRequest? request)
    {
        if (request is null
            || request.Scope is not (CovenantScope.Global or CovenantScope.Campaign)
            || request.Key is null
            || request.EntryId == Guid.Empty
            || request.MutationId == Guid.Empty
            || !HeadShape(request.Confirmed)
            || !HeadShape(request.Proposed))
        {
            return InvalidBody;
        }

        bool paired = request.Scope is CovenantScope.Campaign
            ? request.CampaignId is { } campaign && campaign != Guid.Empty
            : request.CampaignId is null;

        if (!paired || (request.Scope is CovenantScope.Global && request.Proposed is not null))
        {
            return InvalidScope;
        }

        string normalizedKey;

        try
        {
            normalizedKey = new CovenantKey(request.Key).Value;
        }
        catch (ArgumentException)
        {
            return InvalidKey;
        }

        CovenantOperationScope scope = request.CampaignId is { } id
            ? CovenantOperationScope.ForCampaign(id)
            : CovenantOperationScope.Global;

        return new Target(
            request,
            scope,
            normalizedKey,
            MemoryErasureIdentity.ForCovenant(request.Scope, request.CampaignId, normalizedKey));
    }

    private static bool HeadShape(CovenantEraseHeadExpectation? head) =>
        head is null || (head.VersionId != Guid.Empty && head.LaneRevision > 0);

    private static bool HeadMatches(CovenantEntryErasureHead? live, CovenantEraseHeadExpectation? expected) =>
        (live, expected) switch
        {
            (null, null) => true,
            ({ } head, { } expectation) => head.VersionId == expectation.VersionId && head.LaneRevision == expectation.LaneRevision,
            _ => false,
        };

    /// <summary>Whether a failure is SQLite's busy or locked answer, which the busy retry handles.</summary>
    private static bool IsBusy(Exception failure)
    {
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite)
            {
                return sqlite.SqliteErrorCode is 5 or 6;
            }
        }

        return false;
    }

    /// <summary>The validated request, its operation scope, its normalized key and its erasure identity.</summary>
    private sealed record Target(
        CovenantErasePrepareRequest Request,
        CovenantOperationScope Scope,
        string Key,
        MemoryErasureIdentity Identity)
    {
        internal Guid EntryId => Request.EntryId;

        internal Guid MutationId => Request.MutationId;
    }

    /// <summary>The entry as the erasing snapshot found it, and its two lane heads.</summary>
    private sealed record Located(
        CovenantEntryErasureSubject Subject,
        CovenantEntryErasureHead? Confirmed,
        CovenantEntryErasureHead? Proposed);

    private sealed record Measurement(
        Located Located,
        CovenantEntryErasureState State,
        CovenantEntryErasureTally Measured,
        CovenantEntryErasureScopeFacts ScopeFacts,
        MemoryErasureExternalExposureDto Exposure,
        MemoryRetainedLocalCopy[] Retained,
        MemoryErasureEffectFacts Facts);

    private sealed record Applied(MemoryErasureReceiptRow Receipt, bool Replayed);

    /// <summary>
    /// A <c>COMMIT</c> that failed in a way that may still have persisted, and whether the rollback that
    /// followed it failed too.
    /// </summary>
    private sealed class UncertainCommitException(Exception commitFailure, bool rollbackFailed)
        : Exception("The erase's commit failed, and its outcome is settled by its receipt.", commitFailure)
    {
        internal bool RollbackFailed { get; } = rollbackFailed;
    }
}
