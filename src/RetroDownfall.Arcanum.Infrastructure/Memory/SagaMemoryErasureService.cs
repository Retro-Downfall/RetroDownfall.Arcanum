using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// Selective hard erasure of one Saga memory and every memory that holds its exact content in its exact
/// scope: prepare measures and issues a five-minute plan, apply replays by receipt or erases in one
/// verified transaction.
/// </summary>
/// <remarks>
/// <para><b>The erased class.</b> A fingerprint suppresses content in a scope, so the erase removes the
/// target and its twins: every memory with byte-identical content in the same scope and Campaign, found
/// by seeking the scope index with the Campaign in each spelling a writer has produced. A stored Campaign
/// or twin id that is not a GUID cannot be fingerprinted exactly, so the erase fails closed rather than
/// matching loosely.</para>
///
/// <para><b>Prepare.</b> The key is opened, and created on an installation with no evidence, before any
/// transaction or lease, because key access can read the OS credential store. One read snapshot then
/// measures the class, its plan, its labels, its retirement suppressions and its exposure. Only after
/// that snapshot closes does a labelled class take a short read lease to learn its dataset generation, so
/// no gate wait ever holds a SQLite snapshot.</para>
///
/// <para><b>Apply.</b> A committed erase answers by receipt before any token is read, so a replay
/// survives expiry and restarts. Otherwise the token must bind this exact request. A labelled class takes
/// the write lease over its one owner scope before <c>BEGIN IMMEDIATE</c>. Inside the transaction the
/// receipt is probed again, the subject is checked, the target and its content binding are compared,
/// labels are proved, the effect is re-measured, and only then are the rows, labels and retirement pair
/// deleted, the fingerprint and receipt recorded, and absence proved before the commit. The result is
/// built from the receipt only after the commit, so an erase that rolled back never reports one.</para>
///
/// <para>Nothing here logs. Errors name no content.</para>
/// </remarks>
internal sealed class SagaMemoryErasureService(
    ArcanumDbContext db,
    IMemoryErasureKeyCreator keyCreator,
    IMemoryErasureKeyProvider keys,
    IMemoryErasureTokenCodec tokens,
    MemoryErasureScrubber scrubber,
    ICovenantOperationGate gate,
    IOperatorAuthorityContextIssuer issuer,
    ICovenantSqliteConnectionInitializer initializer,
    IOptionsMonitor<ArcanumSettings> options) : ISagaMemoryErasureService
{
    private const int ScrubPending = 1;

    private const int ContentHashHexLength = 64;

    private static readonly Error InvalidBody = new(
        ErrorCodes.Validation.InvalidBody,
        "A Saga erase names a GUID memory id, a 64-character hexadecimal content hash, an optional GUID claim version, a mutation id and, to apply, a preflight token.");

    private static readonly Error NotFound = new(
        ErrorCodes.Saga.NotFound,
        "No Saga memory exists with that id.");

    private static readonly Error StaleContent = new(
        ErrorCodes.Saga.StaleContent,
        "The memory's content or claim is not the one this erase expected; show it again before erasing it.");

    private static readonly Error SubjectErased = new(
        ErrorCodes.MemoryErasure.SubjectErased,
        "This memory was already erased.");

    private static readonly Error StalePlan = new(
        ErrorCodes.MemoryErasure.StalePlan,
        "What this erase would remove changed after it was prepared; prepare it again.");

    private static readonly Error Unidentifiable = new(
        ErrorCodes.MemoryErasure.ErasureIncomplete,
        "The memory's stored identity cannot be named exactly, so nothing was erased.");

    private static readonly Error UnseekableTwin = new(
        ErrorCodes.MemoryErasure.ErasureIncomplete,
        "A memory with this content in this scope is stored under a Campaign spelling the erase cannot seek, so nothing was erased.");

    private static readonly Error SharedMemberId = new(
        ErrorCodes.MemoryErasure.ErasureIncomplete,
        "Another memory shares the identity of one this erase would remove, so nothing was erased.");

    private static readonly Error NotAbsent = new(
        ErrorCodes.MemoryErasure.ErasureIncomplete,
        "The erase could not prove every planned row absent, so it rolled back and recorded nothing.");

    private static readonly Error SplitOwners = new(
        ErrorCodes.Covenant.ForbiddenAuthority,
        "The sensitivity labels of these memories name more than one owner scope, and one erase holds one write lease.");

    private static readonly Error UncoveredLabel = new(
        ErrorCodes.Covenant.ForbiddenAuthority,
        "A sensitivity label on these memories belongs to a scope the held write lease does not cover.");

    private static readonly Error GenerationUnavailable = new(
        ErrorCodes.Covenant.Unavailable,
        "The Covenant dataset that owns these memories' labels is unavailable, so the erase cannot be planned.");

    /// <summary>A Saga request names no scope, so an apply and its replay state the one note every erase does.</summary>
    private static readonly MemoryErasureNote[] ApplyNotes =
        MemoryErasureNotes.For(MemoryReviewStore.Saga, MemoryErasureScopeKind.Global, reclaimsKey: false);

    /// <summary>Test seam: replaces the transaction's <c>COMMIT</c> when set.</summary>
    internal Func<SqliteTransaction, CancellationToken, Task>? CommitForTesting { get; init; }

    /// <summary>Test seam: runs once the pre-transaction receipt probe has found no receipt.</summary>
    internal Func<CancellationToken, Task>? AfterReceiptProbeForTesting { get; init; }

    public async Task<Result<MemoryErasurePreflightDto>> PrepareAsync(
        SagaErasePrepareRequest request,
        CancellationToken cancellationToken)
    {
        Result<Target> parsed = Parse(request?.MemoryId, request?.ExpectedContentHash, request?.ExpectedClaimVersionId, request?.MutationId);

        if (parsed.IsFailure)
        {
            return parsed.Error;
        }

        Target target = parsed.Value;

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        Result installed = await MemoryErasureProtocol.RequireInstalledAsync(connection, cancellationToken).ConfigureAwait(false);

        if (installed.IsFailure)
        {
            return installed.Error;
        }

        // Before any transaction or lease: opening the key can read, and on a fresh installation write,
        // the OS credential store.
        Result<MemoryErasureKey> opened = await MemoryErasureProtocol
            .OpenKeyForPrepareAsync(connection, keyCreator, MemoryReviewStore.Saga, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        using MemoryErasureKey key = opened.Value;

        Result<Measurement> measured = await SqliteBusyRetry.ExecuteAsync<Result<Measurement>>(
            async () =>
            {
                await using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);

                Result<TargetRow?> row = await ReadTargetAsync(connection, snapshot, target, cancellationToken).ConfigureAwait(false);

                if (row.IsFailure)
                {
                    return row.Error;
                }

                if (row.Value is not { } live)
                {
                    return await MemoryErasureEvidence
                        .SubjectErasedAsync(connection, snapshot, key.Subject(MemoryReviewStore.Saga, target.MemoryId), cancellationToken)
                        .ConfigureAwait(false)
                        ? SubjectErased
                        : NotFound;
                }

                if (!ContentMatches(live, target))
                {
                    return StaleContent;
                }

                Result<bool> claim = await ClaimMatchesAsync(connection, snapshot, target, cancellationToken).ConfigureAwait(false);

                if (claim.IsFailure)
                {
                    return claim.Error;
                }

                return claim.Value
                    ? await MeasureAsync(connection, snapshot, live, cancellationToken).ConfigureAwait(false)
                    : StaleContent;
            },
            cancellationToken).ConfigureAwait(false);

        if (measured.IsFailure)
        {
            return measured.Error;
        }

        Measurement plan = measured.Value;

        // Only now that the snapshot is closed: a labelled class records the dataset generation its
        // labels belong to, so apply can refuse a plan measured against a dataset since replaced.
        Guid? generation = null;

        if (plan.Owner is { } owner)
        {
            Result<CovenantReadLease> lease = await gate.AcquireReadAsync(owner, cancellationToken).ConfigureAwait(false);

            if (lease.IsFailure)
            {
                return lease.Error;
            }

            await using (lease.Value)
            {
                generation = lease.Value.Snapshot.DatasetGeneration;
            }

            if (generation is null)
            {
                return GenerationUnavailable;
            }
        }

        byte[] requestDigest = key.SagaRequest(target.MutationId, target.MemoryId, target.ClaimVersion);

        byte[] effectDigest = key.Effect(plan.Facts);

        Result<MemoryErasureIssuedToken> issued = tokens.IssueErasurePlan(new MemoryErasurePlanTokenFacts(
            MemoryReviewStore.Saga,
            requestDigest,
            effectDigest,
            key.SagaContentBinding(target.MemoryId, plan.Content),
            generation));

        if (issued.IsFailure)
        {
            return issued.Error;
        }

        return new MemoryErasurePreflightDto(
            MemoryReviewStore.Saga,
            target.MutationId,
            Convert.ToHexStringLower(requestDigest),
            Convert.ToHexStringLower(effectDigest),
            new MemoryErasurePlanDto(
                plan.Members.Count,
                plan.Facts.Targets.Sum(static table => table.Rows),
                plan.Labels.Count,
                plan.RetirementSuppressions,
                plan.Pinned,
                Lexicon: null,
                Covenant: null),
            plan.Exposure,
            plan.Retained,
            MemoryErasureNotes.For(MemoryReviewStore.Saga, plan.Identity.Scope, reclaimsKey: false),
            issued.Value.IssuedAtUtc,
            issued.Value.ExpiresAtUtc,
            issued.Value.Token);
    }

    public async Task<Result<MemoryErasureResultDto>> ApplyAsync(
        SagaEraseRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);

        Result<Target> parsed = Parse(request?.MemoryId, request?.ExpectedContentHash, request?.ExpectedClaimVersionId, request?.MutationId);

        if (parsed.IsFailure || string.IsNullOrWhiteSpace(request!.PreflightToken))
        {
            return InvalidBody;
        }

        Target target = parsed.Value;

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        Result installed = await MemoryErasureProtocol.RequireInstalledAsync(connection, cancellationToken).ConfigureAwait(false);

        if (installed.IsFailure)
        {
            return installed.Error;
        }

        Result<MemoryErasureKey> opened = await MemoryErasureProtocol
            .OpenKeyForApplyAsync(connection, keys, MemoryReviewStore.Saga, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        using MemoryErasureKey key = opened.Value;

        // From the request alone, so a committed erase can answer before any token is read.
        byte[] requestDigest = key.SagaRequest(target.MutationId, target.MemoryId, target.ClaimVersion);

        Result<MemoryErasureReceiptRow?> recorded = await MemoryErasureProtocol
            .ProbeReceiptAsync(connection, null, MemoryReviewStore.Saga, target.MutationId, requestDigest, cancellationToken)
            .ConfigureAwait(false);

        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value is { } committed)
        {
            return await MemoryErasureProtocol
                .FinishAsync(connection, scrubber, committed, replayed: true, ApplyNotes, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (AfterReceiptProbeForTesting is { } afterProbe)
        {
            await afterProbe(cancellationToken).ConfigureAwait(false);
        }

        Result<MemoryErasurePlanTokenFacts> read = tokens.ReadErasurePlan(request.PreflightToken);

        if (read.IsFailure
            || read.Value.Store is not MemoryReviewStore.Saga
            || read.Value.ContentBinding is null
            || !CryptographicOperations.FixedTimeEquals(read.Value.RequestDigest, requestDigest))
        {
            return MemoryErasureProtocol.InvalidPreflightError;
        }

        MemoryErasurePlanTokenFacts plan = read.Value;

        Result<Applied> applied;

        CovenantWriteLease? lease = null;

        try
        {
            CovenantArtifactErasureAuthority? erasure = null;

            // A labelled plan takes the one write lease over its labels' owner before BEGIN, and the lease
            // must still see the dataset the labels were measured in. A class that no longer exists, or
            // no longer carries a label, takes no lease: the transaction answers it, replaying a receipt
            // that committed meanwhile, refusing an erased subject with 410 and a missing row with 404,
            // and refusing anything else that changed as a stale plan.
            Result<CovenantOperationScope?> owner = plan.DatasetGeneration is null
                ? Result<CovenantOperationScope?>.Success(null)
                : await ReadOwnerAsync(connection, target, cancellationToken).ConfigureAwait(false);

            if (owner.IsFailure)
            {
                return owner.Error;
            }

            if (plan.DatasetGeneration is { } generation && owner.Value is { } labelOwner)
            {
                Result<CovenantWriteLease> acquired = await gate.AcquireWriteAsync(labelOwner, cancellationToken).ConfigureAwait(false);

                if (acquired.IsFailure)
                {
                    return acquired.Error;
                }

                lease = acquired.Value;

                Result<CovenantArtifactErasureAuthority> borrowed = CovenantArtifactErasureAuthority.ForOrdinary(lease, authority, issuer);

                if (borrowed.IsFailure)
                {
                    return borrowed.Error;
                }

                erasure = borrowed.Value;

                if (lease.Snapshot.DatasetGeneration != generation)
                {
                    return StalePlan;
                }
            }

            try
            {
                applied = await SqliteBusyRetry.ExecuteAsync(
                    () => ApplyInTransactionAsync(connection, key, target, requestDigest, plan, erasure, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (UncertainCommitException uncertain)
            {
                // The transaction is disposed, so a commit that did not persist has been rolled back.
                // Only the receipt can say which happened.
                applied = await SettleUncertainCommitAsync(target, requestDigest, uncertain).ConfigureAwait(false);
            }
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (applied.IsFailure)
        {
            return applied.Error;
        }

        // Reached only after a successful COMMIT, or for a receipt the transaction found already
        // committed: the transaction and the lease are both disposed, and a refused erase returned above.
        return await MemoryErasureProtocol
            .FinishAsync(connection, scrubber, applied.Value.Receipt, applied.Value.Replayed, ApplyNotes, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The erase itself: one <c>BEGIN IMMEDIATE</c> that re-proves everything the plan assumed, removes
    /// the class, records the evidence, proves absence, and commits.
    /// </summary>
    private async Task<Result<Applied>> ApplyInTransactionAsync(
        SqliteConnection connection,
        MemoryErasureKey key,
        Target target,
        byte[] requestDigest,
        MemoryErasurePlanTokenFacts plan,
        CovenantArtifactErasureAuthority? erasure,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        // A concurrent identical apply may have committed while this one waited for the write lock.
        Result<MemoryErasureReceiptRow?> recorded = await MemoryErasureProtocol
            .ProbeReceiptAsync(connection, transaction, MemoryReviewStore.Saga, target.MutationId, requestDigest, cancellationToken)
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
                .SubjectErasedAsync(connection, transaction, key.Subject(MemoryReviewStore.Saga, target.MemoryId), cancellationToken)
                .ConfigureAwait(false))
        {
            return SubjectErased;
        }

        Result<TargetRow?> row = await ReadTargetAsync(connection, transaction, target, cancellationToken).ConfigureAwait(false);

        if (row.IsFailure)
        {
            return row.Error;
        }

        if (row.Value is not { } live)
        {
            return NotFound;
        }

        if (!ContentMatches(live, target))
        {
            return StaleContent;
        }

        // The binding is compared before the claim: a claimless memory corrected since prepare may have
        // opened a claim on the way, and what makes this erase wrong is that the text it planned against
        // is gone.
        if (!CryptographicOperations.FixedTimeEquals(key.SagaContentBinding(target.MemoryId, live.Content), plan.ContentBinding))
        {
            return StalePlan;
        }

        Result<bool> claim = await ClaimMatchesAsync(connection, transaction, target, cancellationToken).ConfigureAwait(false);

        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (!claim.Value)
        {
            return StaleContent;
        }

        Result<Measurement> measured = await MeasureAsync(connection, transaction, live, cancellationToken).ConfigureAwait(false);

        if (measured.IsFailure)
        {
            return measured.Error;
        }

        Measurement erasing = measured.Value;

        // The label proof: a plan that measured no label may remove none, and a labelled plan removes
        // only labels its held lease covers.
        if (erasure is null && erasing.Labels.Count > 0)
        {
            return StalePlan;
        }

        foreach (MemoryErasureLabelRow label in erasing.Labels)
        {
            if (erasure is not null && !erasure.Covers(OwnerOf(label)))
            {
                return UncoveredLabel;
            }
        }

        byte[] effectDigest = key.Effect(erasing.Facts);

        if (!CryptographicOperations.FixedTimeEquals(effectDigest, plan.EffectDigest))
        {
            return StalePlan;
        }

        long removedRows = 0;

        bool legacyMirror = false;

        foreach (Member member in erasing.Members)
        {
            CovenantArtifactPlanTally removed = await CovenantArtifactPlanRunner
                .RunAsync(connection, transaction, SensitiveArtifactKind.Saga, member.Key, CovenantArtifactPlanMode.Delete, cancellationToken)
                .ConfigureAwait(false);

            removedRows += removed.Targets.Sum(static table => table.Rows);

            legacyMirror |= removed.VectorMirror is SagaVectorMirrorKind.LegacyVirtualTable;
        }

        int removedLabels = 0;

        foreach (MemoryErasureLabelRow label in erasing.Labels)
        {
            removedLabels += await MemoryErasureLabels
                .DeleteExactAsync(connection, transaction, initializer, label.LabelId, label.ArtifactId, cancellationToken)
                .ConfigureAwait(false);
        }

        int removedSuppressions = await SagaRetirementSuppression
            .DeletePairAsync(connection, transaction, live.ScopeKind, live.CampaignId, live.Content, cancellationToken)
            .ConfigureAwait(false);

        _ = await MemoryErasureEvidence
            .InsertFingerprintAsync(
                connection,
                transaction,
                key.Fingerprint(erasing.Identity),
                MemoryReviewStore.Saga,
                key.KeyId.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<MemoryExternalEvidence> evidence = erasing.Facts.Evidence;

        MemoryErasureReceiptRow receipt = new(
            target.MutationId,
            MemoryReviewStore.Saga,
            key.KeyId.ToArray(),
            requestDigest,
            effectDigest,
            erasing.Members.Count,
            removedRows,
            removedLabels,
            removedSuppressions,
            evidence[0],
            evidence[1],
            evidence[2],
            evidence[3],
            evidence[4],
            erasing.Facts.RetainedCopiesMask,
            ScrubPending,
            MemoryErasureScrubPendingReasons.ToMask(
                legacyMirror
                    ? [MemoryErasureScrubPendingReason.WalCheckpointPending, MemoryErasureScrubPendingReason.VectorIndexScrubUnverified]
                    : [MemoryErasureScrubPendingReason.WalCheckpointPending]));

        await MemoryErasureEvidence
            .InsertReceiptAsync(
                connection,
                transaction,
                receipt,
                [.. erasing.Members.Select(member => key.Subject(MemoryReviewStore.Saga, member.Id))],
                cancellationToken)
            .ConfigureAwait(false);

        if (!await ProveAbsentAsync(connection, transaction, live, erasing, cancellationToken).ConfigureAwait(false))
        {
            return NotAbsent;
        }

        if (erasure is not null)
        {
            Result current = await erasure.RevalidateAsync(cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }
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
            throw new UncertainCommitException(failure);
        }

        return new Applied(receipt, Replayed: false);
    }

    /// <summary>
    /// Settles a commit that failed: the receipt read back on a fresh connection is an erase that
    /// happened, and its absence, or a read that cannot be made, reports the commit's own failure.
    /// </summary>
    private async Task<Applied> SettleUncertainCommitAsync(
        Target target,
        byte[] requestDigest,
        UncertainCommitException uncertain)
    {
        Result<MemoryErasureReceiptRow?> reread = await scrubber
            .ReadCommittedReceiptAsync(target.MutationId, CancellationToken.None)
            .ConfigureAwait(false);

        if (reread.IsSuccess
            && reread.Value is { Store: MemoryReviewStore.Saga } persisted
            && CryptographicOperations.FixedTimeEquals(persisted.RequestDigest, requestDigest))
        {
            return new Applied(persisted, Replayed: false);
        }

        ExceptionDispatchInfo.Capture(uncertain.InnerException!).Throw();

        throw new UnreachableException();
    }

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

    /// <summary>
    /// The authoritative absence proof: every planned row counted again, inside the erase's own
    /// transaction, with the predicates that deleted it.
    /// </summary>
    private static async Task<bool> ProveAbsentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TargetRow live,
        Measurement erasing,
        CancellationToken cancellationToken)
    {
        foreach (Member member in erasing.Members)
        {
            CovenantArtifactPlanTally left = await CovenantArtifactPlanRunner
                .RunAsync(connection, transaction, SensitiveArtifactKind.Saga, member.Key, CovenantArtifactPlanMode.Count, cancellationToken)
                .ConfigureAwait(false);

            if (left.Targets.Any(static table => table.Rows != 0))
            {
                return false;
            }
        }

        IReadOnlyList<MemoryErasureLabelRow> labels = await MemoryErasureLabels
            .ReadAsync(connection, transaction, SensitiveArtifactKind.Saga, [.. erasing.Members.Select(static member => member.Id)], cancellationToken)
            .ConfigureAwait(false);

        return labels.Count == 0
            && await SagaRetirementSuppression
                .CountPairAsync(connection, transaction, live.ScopeKind, live.CampaignId, live.Content, cancellationToken)
                .ConfigureAwait(false) == 0;
    }

    /// <summary>
    /// Finds the one owner scope of the class's labels outside any transaction, so the write lease can be
    /// taken before <c>BEGIN</c>.
    /// </summary>
    /// <remarks>
    /// A class that no longer exists, or no longer carries a label, has no owner, and the transaction
    /// decides what that means: a replay, 410, 404, or a stale plan. Answering here would pre-empt the
    /// receipt re-probe and the subject check. Labels that name two owners are refused, because one
    /// erase holds one write lease.
    /// </remarks>
    private static async Task<Result<CovenantOperationScope?>> ReadOwnerAsync(
        SqliteConnection connection,
        Target target,
        CancellationToken cancellationToken) =>
        await SqliteBusyRetry.ExecuteAsync<Result<CovenantOperationScope?>>(
            async () =>
            {
                await using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);

                Result<TargetRow?> row = await ReadTargetAsync(connection, snapshot, target, cancellationToken).ConfigureAwait(false);

                if (row.IsFailure)
                {
                    return row.Error;
                }

                if (row.Value is not { } live)
                {
                    return Result<CovenantOperationScope?>.Success(null);
                }

                Result<ClassMembers> members = await ReadClassAsync(connection, snapshot, live, cancellationToken).ConfigureAwait(false);

                if (members.IsFailure)
                {
                    return members.Error;
                }

                IReadOnlyList<MemoryErasureLabelRow> labels = await MemoryErasureLabels
                    .ReadAsync(connection, snapshot, SensitiveArtifactKind.Saga, [.. members.Value.Members.Select(static member => member.Id)], cancellationToken)
                    .ConfigureAwait(false);

                return SingleOwner(labels);
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Measures everything the effect digest binds over the target's class, in the caller's snapshot.
    /// </summary>
    private async Task<Result<Measurement>> MeasureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TargetRow live,
        CancellationToken cancellationToken)
    {
        Result<ClassMembers> read = await ReadClassAsync(connection, transaction, live, cancellationToken).ConfigureAwait(false);

        if (read.IsFailure)
        {
            return read.Error;
        }

        ClassMembers found = read.Value;

        List<Guid?> versions = new(found.Members.Count);

        foreach (Member member in found.Members)
        {
            Result<Guid?> version = await ReadClaimVersionAsync(connection, transaction, member.Key, cancellationToken).ConfigureAwait(false);

            if (version.IsFailure)
            {
                return version.Error;
            }

            versions.Add(version.Value);
        }

        // The per-table sums across the class, in the runner's own order. The runner already lists the
        // artifact table once, last, so nothing is appended to them.
        List<MemoryErasureTableCount>? targets = null;

        foreach (Member member in found.Members)
        {
            CovenantArtifactPlanTally tally = await CovenantArtifactPlanRunner
                .RunAsync(connection, transaction, SensitiveArtifactKind.Saga, member.Key, CovenantArtifactPlanMode.Count, cancellationToken)
                .ConfigureAwait(false);

            // A member's normalised id names exactly its own row. A second row under it, in any scope,
            // would be deleted with the member.
            if (tally.ArtifactRows != 1)
            {
                return SharedMemberId;
            }

            if (targets is null)
            {
                targets = [.. tally.Targets];

                continue;
            }

            for (int index = 0; index < targets.Count; index++)
            {
                MemoryErasureTableCount table = tally.Targets[index];

                if (!string.Equals(targets[index].Table, table.Table, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The Saga plan listed its targets in a different order for one member of the class.");
                }

                targets[index] = targets[index] with { Rows = targets[index].Rows + table.Rows };
            }
        }

        IReadOnlyList<MemoryErasureLabelRow> labels = await MemoryErasureLabels
            .ReadAsync(connection, transaction, SensitiveArtifactKind.Saga, [.. found.Members.Select(static member => member.Id)], cancellationToken)
            .ConfigureAwait(false);

        Result<CovenantOperationScope?> owner = SingleOwner(labels);

        if (owner.IsFailure)
        {
            return owner.Error;
        }

        int suppressions = await SagaRetirementSuppression
            .CountPairAsync(connection, transaction, live.ScopeKind, live.CampaignId, live.Content, cancellationToken)
            .ConfigureAwait(false);

        MemoryErasureExternalExposureDto exposure = await MemoryErasureExposure
            .ReadSagaAsync(connection, transaction, [.. found.Members.Select(static member => member.Id)], cancellationToken)
            .ConfigureAwait(false);

        MemoryRetainedLocalCopy[] retained = MemoryErasureRetainedCopies.For(
            MemoryReviewStore.Saga,
            MemoryErasureRetainedCopies.AuditFilesExist(options.CurrentValue));

        bool pinned = found.Members.Any(static member => member.Pinned);

        MemoryErasureEffectFacts facts = new(
            MemoryReviewStore.Saga,
            [.. found.Members.Select(static member => member.Id)],
            versions,
            targets ?? [],
            labels.Count,
            suppressions,
            pinned ? MemoryErasureEffectFlags.Pinned : MemoryErasureEffectFlags.None,
            Covenant: null,
            MemoryErasureExposure.EvidenceCodes(exposure),
            MemoryRetainedLocalCopies.ToMask(retained));

        return new Measurement(
            live.Content,
            found.Identity,
            found.Members,
            labels,
            owner.Value,
            suppressions,
            pinned,
            exposure,
            retained,
            facts);
    }

    /// <summary>
    /// The target and its twins: byte-identical content in the target's exact scope and Campaign.
    /// </summary>
    /// <remarks>
    /// <para>The scope index is sought with the Campaign bound in each spelling a writer has produced:
    /// upper- and lower-case, dashed and undashed. A stored Campaign that is not a GUID, a twin whose id
    /// is not one, or a class that somehow does not contain its own target fails closed.</para>
    ///
    /// <para>The seek is then checked against a count of the same scope and content that compares the
    /// Campaign normalised, which reads every spelling. A twin stored in a spelling the seek missed would
    /// be suppressed by the fingerprint and left live by the delete, so a count the seek does not match
    /// fails closed before anything is deleted.</para>
    /// </remarks>
    private static async Task<Result<ClassMembers>> ReadClassAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TargetRow live,
        CancellationToken cancellationToken)
    {
        MemoryErasureIdentity identity;

        try
        {
            identity = SagaErasureWriteGate.SagaIdentity(live.ScopeKind, live.CampaignId, live.Content);
        }
        catch (FormatException)
        {
            return Unidentifiable;
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        if (identity.CampaignId is { } campaign)
        {
            command.CommandText =
                """
                SELECT Id, PinnedAtUtc IS NOT NULL
                FROM saga_memories
                WHERE ScopeKindCode = $scope
                  AND CampaignId IN ($upper, $lower, $upperN, $lowerN)
                  AND Content = $content;
                """;

            _ = command.Parameters.AddWithValue("$upper", campaign.ToString("D").ToUpperInvariant());

            _ = command.Parameters.AddWithValue("$lower", campaign.ToString("D"));

            _ = command.Parameters.AddWithValue("$upperN", campaign.ToString("N").ToUpperInvariant());

            _ = command.Parameters.AddWithValue("$lowerN", campaign.ToString("N"));
        }
        else
        {
            command.CommandText =
                """
                SELECT Id, PinnedAtUtc IS NOT NULL
                FROM saga_memories
                WHERE ScopeKindCode = $scope
                  AND CampaignId IS NULL
                  AND Content = $content;
                """;
        }

        _ = command.Parameters.AddWithValue("$scope", (int)live.ScopeKind);

        _ = command.Parameters.AddWithValue("$content", live.Content);

        List<Member> members = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string id = reader.GetString(0);

                if (!Guid.TryParse(id, CultureInfo.InvariantCulture, out Guid parsed))
                {
                    return Unidentifiable;
                }

                members.Add(new Member(id, CovenantIdentitySql.Key(parsed), reader.GetInt64(1) == 1));
            }
        }

        if (!members.Any(member => string.Equals(member.Id, live.Id, StringComparison.Ordinal)))
        {
            return Unidentifiable;
        }

        if (await CountClassAsync(connection, transaction, live, identity, cancellationToken).ConfigureAwait(false) != members.Count)
        {
            return UnseekableTwin;
        }

        members.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        return new ClassMembers(identity, members);
    }

    /// <summary>
    /// Every row of the class's scope and content, with the Campaign compared normalised rather than
    /// sought by spelling.
    /// </summary>
    private static async Task<long> CountClassAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TargetRow live,
        MemoryErasureIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        if (identity.CampaignId is { } campaign)
        {
            command.CommandText =
                $"""
                SELECT count(*)
                FROM saga_memories
                WHERE ScopeKindCode = $scope
                  AND {CovenantIdentitySql.Keyed("CampaignId", "$campaign")}
                  AND Content = $content;
                """;

            _ = command.Parameters.AddWithValue("$campaign", CovenantIdentitySql.Key(campaign));
        }
        else
        {
            command.CommandText =
                """
                SELECT count(*)
                FROM saga_memories
                WHERE ScopeKindCode = $scope
                  AND CampaignId IS NULL
                  AND Content = $content;
                """;
        }

        _ = command.Parameters.AddWithValue("$scope", (int)live.ScopeKind);

        _ = command.Parameters.AddWithValue("$content", live.Content);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rows
            ? rows
            : throw new InvalidDataException("The erased class's cross-check count did not return an integer.");
    }

    /// <summary>The target row by its normalised id, or null when no row has that id.</summary>
    private static async Task<Result<TargetRow?>> ReadTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Target target,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"""
            SELECT Id, Content, ScopeKindCode, CampaignId
            FROM saga_memories
            WHERE {CovenantIdentitySql.Keyed("Id", "$id")};
            """;

        _ = command.Parameters.AddWithValue("$id", CovenantIdentitySql.Key(target.Guid));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<TargetRow?>.Success(null);
        }

        TargetRow row = new(
            reader.GetString(0),
            reader.GetString(1),
            (SagaMemoryScopeKind)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));

        // Two rows under one normalised id would make "the target" ambiguous.
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Result<TargetRow?>.Failure(Unidentifiable)
            : Result<TargetRow?>.Success(row);
    }

    /// <summary>Whether the target's claim head is the version the caller expected, where none means claimless.</summary>
    private static async Task<Result<bool>> ClaimMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Target target,
        CancellationToken cancellationToken)
    {
        Result<Guid?> version = await ReadClaimVersionAsync(
            connection,
            transaction,
            CovenantIdentitySql.Key(target.Guid),
            cancellationToken).ConfigureAwait(false);

        return version.IsFailure
            ? Result<bool>.Failure(version.Error)
            : Result<bool>.Success(version.Value == target.ClaimVersion);
    }

    /// <summary>One memory's claim head version, or null when the memory has no claim.</summary>
    private static async Task<Result<Guid?>> ReadClaimVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string memoryKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            $"""
            SELECT head.CurrentVersionId
            FROM annal_heads AS head
            JOIN annal_claims AS claim ON claim.ClaimId = head.ClaimId
            WHERE claim.SubjectStoreCode = {(int)AnnalSubjectStore.Saga}
              AND {CovenantIdentitySql.Keyed("claim.SubjectId", "$memory")};
            """;

        _ = command.Parameters.AddWithValue("$memory", memoryKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<Guid?>.Success(null);
        }

        bool parsed = Guid.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out Guid version);

        return !parsed || await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Result<Guid?>.Failure(Unidentifiable)
            : Result<Guid?>.Success(version);
    }

    /// <summary>The one owner scope every label names, null when there are none, or a refusal when they disagree.</summary>
    private static Result<CovenantOperationScope?> SingleOwner(IReadOnlyList<MemoryErasureLabelRow> labels)
    {
        if (labels.Count == 0)
        {
            return Result<CovenantOperationScope?>.Success(null);
        }

        Guid? campaign = labels[0].OwnerCampaignId;

        return labels.All(label => label.OwnerCampaignId == campaign)
            ? Result<CovenantOperationScope?>.Success(OwnerOf(labels[0]))
            : Result<CovenantOperationScope?>.Failure(SplitOwners);
    }

    private static CovenantOperationScope OwnerOf(MemoryErasureLabelRow label) =>
        label.OwnerCampaignId is { } campaign
            ? CovenantOperationScope.ForCampaign(campaign)
            : CovenantOperationScope.Global;

    /// <summary>Whether the live content is the content the caller hashed, compared in constant time.</summary>
    private static bool ContentMatches(TargetRow live, Target target) =>
        CryptographicOperations.FixedTimeEquals(AnnalContentDigest.ForSagaMemory(live.Content), target.ContentHash);

    /// <summary>Refuses anything but a GUID memory id, a SHA-256 hex hash, an optional GUID claim and a mutation id.</summary>
    private static Result<Target> Parse(string? memoryId, string? contentHash, string? claimVersionId, Guid? mutationId)
    {
        if (memoryId is null
            || !Guid.TryParse(memoryId, CultureInfo.InvariantCulture, out Guid memory)
            || contentHash is not { Length: ContentHashHexLength }
            || !contentHash.All(char.IsAsciiHexDigit)
            || mutationId is not { } mutation
            || mutation == Guid.Empty)
        {
            return InvalidBody;
        }

        Guid? claim = null;

        if (claimVersionId is not null)
        {
            if (!Guid.TryParse(claimVersionId, CultureInfo.InvariantCulture, out Guid parsedClaim))
            {
                return InvalidBody;
            }

            claim = parsedClaim;
        }

        return new Target(memoryId, memory, Convert.FromHexString(contentHash), claim, mutation);
    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>The validated request: the id as the caller spelled it, and everything parsed out of it.</summary>
    private sealed record Target(string MemoryId, Guid Guid, byte[] ContentHash, Guid? ClaimVersion, Guid MutationId);

    /// <summary>The target row as stored.</summary>
    private sealed record TargetRow(string Id, string Content, SagaMemoryScopeKind ScopeKind, string? CampaignId);

    /// <summary>One member of the erased class: its stored id, its normalised key, and whether it is pinned.</summary>
    private sealed record Member(string Id, string Key, bool Pinned);

    private sealed record ClassMembers(MemoryErasureIdentity Identity, IReadOnlyList<Member> Members);

    private sealed record Measurement(
        string Content,
        MemoryErasureIdentity Identity,
        IReadOnlyList<Member> Members,
        IReadOnlyList<MemoryErasureLabelRow> Labels,
        CovenantOperationScope? Owner,
        int RetirementSuppressions,
        bool Pinned,
        MemoryErasureExternalExposureDto Exposure,
        MemoryRetainedLocalCopy[] Retained,
        MemoryErasureEffectFacts Facts);

    private sealed record Applied(MemoryErasureReceiptRow Receipt, bool Replayed);

    /// <summary>A <c>COMMIT</c> that failed in a way that may still have persisted.</summary>
    private sealed class UncertainCommitException(Exception commitFailure)
        : Exception("The erase's commit failed, and its outcome is settled by its receipt.", commitFailure);
}
