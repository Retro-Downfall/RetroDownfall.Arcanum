using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

/// <summary>What a Lexicon erase needs beyond the service's own connection and key provider.</summary>
/// <remarks>
/// Composed only in the host, because prepare may create the erasure key and only the host may. A
/// service composed without it answers both erase verbs as unavailable. It has no key provider of its
/// own: the verbs use the one every Lexicon write already holds.
/// </remarks>
internal sealed record LexiconErasureDependencies(
    IMemoryErasureKeyCreator KeyCreator,
    IMemoryErasureTokenCodec Tokens,
    MemoryErasureScrubber Scrubber,
    ICovenantOperationGate Gate,
    IOperatorAuthorityContextIssuer Issuer,
    ICovenantSqliteConnectionInitializer Initializer);

/// <summary>The Unseen Servant's own Lexicon entries, recognized by name.</summary>
/// <remarks>
/// A daemon job keeps its working state under <c>daemon_state:</c> names. Those entries are the job's
/// to manage: the agent cannot delete them and the operator cannot erase them.
/// </remarks>
internal static class LexiconDaemonStateNames
{
    private const string Prefix = "daemon_state:";

    internal static bool Is(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Selective hard erasure of one exact Lexicon entry: prepare measures it against the complete target
/// show reported and issues a five-minute plan, and apply replays by receipt or erases in one verified
/// transaction.
/// </summary>
/// <remarks>
/// <para><b>The erased item.</b> One entry in one exact scope, with its current fact provenance, its
/// Annals claim and every row that hangs off it, including historical fact provenance, and any
/// sensitivity label. Its full-text rows go with the entry through the index's own delete trigger. A
/// name has no twins: <c>(ScopeCampaignId, NameNormalized)</c> is unique. The fingerprint names the
/// entry's name, trimmed and upper-cased, in that exact scope, so a Global entry and a Campaign entry
/// of the same name are different subjects.</para>
///
/// <para><b>Prepare.</b> The key is opened, and created on an installation with no evidence, before any
/// transaction, because key access can read the OS credential store. One read snapshot then compares
/// the live entry with the target and measures the plan, the labels, whether a Global entry of the
/// same name would resurface, and the exposure. Only after that snapshot closes does a labelled entry
/// take a short read lease to learn its dataset generation.</para>
///
/// <para><b>Apply.</b> A committed erase answers by receipt before any token is read. Otherwise the token
/// must bind this exact request. A labelled entry takes the write lease over its own scope before
/// <c>BEGIN IMMEDIATE</c>; an entry already gone takes none, and the transaction answers it. Inside the
/// transaction the receipt is probed again and the subject checked, the full-text index is made to
/// scrub what it deletes, the target is compared, labels are proved, the effect is re-measured, and only
/// then are the rows and label deleted, the fingerprint and receipt recorded, and absence proved,
/// the entry's full-text row included, before the commit. The result is built from the receipt only after a commit that succeeded, or one whose
/// receipt a fresh connection can read back.</para>
///
/// <para>Like every Lexicon write it runs under a raw <c>BEGIN</c> on the scoped connection, so the
/// curation reads it shares with correction and lifecycle changes run unchanged, and the evidence, plan
/// and label calls take no transaction object. Nothing here logs. Errors name no content.</para>
/// </remarks>
internal sealed partial class LexiconService : ILexiconErasureService
{
    private const int ErasureScrubPending = 1;

    private static readonly Error ErasureInvalidBody = new(
        ErrorCodes.Validation.InvalidBody,
        "A Lexicon erase names the complete target that show reported, a mutation id and, to apply, a preflight token.");

    private static readonly Error ErasureInvalidTarget = new(
        ErrorCodes.Lexicon.InvalidCurationTarget,
        "A complete Lexicon target is required.");

    private static readonly Error DaemonStateErasureRefused = new(
        ErrorCodes.Lexicon.InvalidName,
        "Unseen Servant daemon_state entries are managed by their daemon job and cannot be erased.");

    private static readonly Error ErasureEntryNotFound = new(
        ErrorCodes.Lexicon.NotFound,
        "Lexicon entity was not found.");

    private static readonly Error ErasureSubjectErased = new(
        ErrorCodes.MemoryErasure.SubjectErased,
        "This Lexicon entry was already erased.");

    private static readonly Error ErasureStalePlan = new(
        ErrorCodes.MemoryErasure.StalePlan,
        "What this erase would remove changed after it was prepared; prepare it again.");

    private static readonly Error ErasureSharedEntryId = new(
        ErrorCodes.MemoryErasure.ErasureIncomplete,
        "Another row shares this entry's identity, so nothing was erased.");

    private static readonly Error ErasureNotAbsent = new(
        ErrorCodes.MemoryErasure.ErasureIncomplete,
        "The erase could not prove every planned row absent, so it rolled back and recorded nothing.");

    private static readonly Error ErasureUncoveredLabel = new(
        ErrorCodes.Covenant.ForbiddenAuthority,
        "The entry's sensitivity label belongs to a scope the held write lease does not cover.");

    private static readonly Error ErasureGenerationUnavailable = new(
        ErrorCodes.Covenant.Unavailable,
        "The Covenant dataset that owns this entry's label is unavailable, so the erase cannot be planned.");

    /// <summary>Test seam: replaces the erase transaction's <c>COMMIT</c> when set.</summary>
    internal Func<DbConnection, CancellationToken, Task>? ErasureCommitForTesting { get; init; }

    public async Task<Result<MemoryErasurePreflightDto>> PrepareAsync(
        LexiconErasePrepareRequest request,
        CancellationToken cancellationToken)
    {
        if (erasure is not { } dependencies)
        {
            return MemoryErasureProtocol.UnavailableError;
        }

        Result<LexiconCurationTarget> checkedTarget = CheckErasureTarget(request?.Target, request?.MutationId);

        if (checkedTarget.IsFailure)
        {
            return checkedTarget.Error;
        }

        LexiconCurationTarget target = checkedTarget.Value;

        Guid mutationId = request!.MutationId;

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        Result installed = await MemoryErasureProtocol.RequireInstalledAsync(connection, cancellationToken).ConfigureAwait(false);

        if (installed.IsFailure)
        {
            return installed.Error;
        }

        // Before any transaction or lease: opening the key can read, and on a fresh installation write,
        // the OS credential store.
        Result<MemoryErasureKey> opened = await MemoryErasureProtocol
            .OpenKeyForPrepareAsync(connection, dependencies.KeyCreator, MemoryReviewStore.Lexicon, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        using MemoryErasureKey key = opened.Value;

        Result<ErasureMeasurement> measured;

        try
        {
            measured = await SqliteBusyRetry.ExecuteAsync<Result<ErasureMeasurement>>(
                async () =>
                {
                    await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN DEFERRED").ConfigureAwait(false);

                    try
                    {
                        Result<ErasureMeasurement> read = await MeasurePreparedErasureAsync(connection, key, target, cancellationToken)
                            .ConfigureAwait(false);

                        await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                        return read;
                    }
                    catch
                    {
                        await TryRollbackAsync(connection, "erasure").ConfigureAwait(false);

                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (InspectionException exception)
        {
            return exception.Error;
        }

        if (measured.IsFailure)
        {
            return measured.Error;
        }

        ErasureMeasurement plan = measured.Value;

        // Only now that the snapshot is closed: a labelled entry records the dataset generation its label
        // belongs to, so apply can refuse a plan measured against a dataset since replaced.
        Guid? generation = null;

        if (target.SensitivityLabel.IsPresent)
        {
            Result<CovenantReadLease> lease = await dependencies.Gate
                .AcquireReadAsync(ErasureOwnerOf(target.Scope), cancellationToken)
                .ConfigureAwait(false);

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
                return ErasureGenerationUnavailable;
            }
        }

        byte[] requestDigest = key.LexiconRequest(mutationId, target);

        byte[] effectDigest = key.Effect(plan.Facts);

        Result<MemoryErasureIssuedToken> issued = dependencies.Tokens.IssueErasurePlan(new MemoryErasurePlanTokenFacts(
            MemoryReviewStore.Lexicon,
            requestDigest,
            effectDigest,
            ContentBinding: null,
            generation));

        if (issued.IsFailure)
        {
            return issued.Error;
        }

        return new MemoryErasurePreflightDto(
            MemoryReviewStore.Lexicon,
            mutationId,
            Convert.ToHexStringLower(requestDigest),
            Convert.ToHexStringLower(effectDigest),
            new MemoryErasurePlanDto(
                ErasedItemCount: 1,
                plan.Facts.Targets.Sum(static table => table.Rows),
                plan.Labels.Count,
                RetirementSuppressionsToRemove: 0,
                plan.Pinned,
                new LexiconErasurePlanFacts(plan.GlobalEntryResurfaces),
                Covenant: null),
            plan.Exposure,
            plan.Retained,
            ErasureNotes(target),
            issued.Value.IssuedAtUtc,
            issued.Value.ExpiresAtUtc,
            issued.Value.Token);
    }

    public async Task<Result<MemoryErasureResultDto>> ApplyAsync(
        LexiconEraseRequest request,
        OperatorAuthorityContext authority,
        CancellationToken cancellationToken)
    {
        if (erasure is not { } dependencies)
        {
            return MemoryErasureProtocol.UnavailableError;
        }

        ArgumentNullException.ThrowIfNull(authority);

        Result<LexiconCurationTarget> checkedTarget = CheckErasureTarget(request?.Target, request?.MutationId);

        if (checkedTarget.IsFailure)
        {
            return checkedTarget.Error;
        }

        if (string.IsNullOrWhiteSpace(request!.PreflightToken))
        {
            return ErasureInvalidBody;
        }

        LexiconCurationTarget target = checkedTarget.Value;

        Guid mutationId = request.MutationId;

        SqliteConnection connection = (SqliteConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        Result installed = await MemoryErasureProtocol.RequireInstalledAsync(connection, cancellationToken).ConfigureAwait(false);

        if (installed.IsFailure)
        {
            return installed.Error;
        }

        Result<MemoryErasureKey> opened = await MemoryErasureProtocol
            .OpenKeyForApplyAsync(connection, erasureKeys, MemoryReviewStore.Lexicon, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        using MemoryErasureKey key = opened.Value;

        // From the request alone, so a committed erase can answer before any token is read.
        byte[] requestDigest = key.LexiconRequest(mutationId, target);

        MemoryErasureNote[] notes = ErasureNotes(target);

        Result<MemoryErasureReceiptRow?> recorded = await MemoryErasureProtocol
            .ProbeReceiptAsync(connection, null, MemoryReviewStore.Lexicon, mutationId, requestDigest, cancellationToken)
            .ConfigureAwait(false);

        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value is { } committed)
        {
            return await MemoryErasureProtocol
                .FinishAsync(connection, dependencies.Scrubber, committed, replayed: true, notes, CancellationToken.None)
                .ConfigureAwait(false);
        }

        Result<MemoryErasurePlanTokenFacts> read = dependencies.Tokens.ReadErasurePlan(request.PreflightToken);

        // The request digest binds the label the target declares, and a prepare records a dataset
        // generation exactly when one is declared, so a token that disagrees was not issued for this.
        if (read.IsFailure
            || read.Value.Store is not MemoryReviewStore.Lexicon
            || read.Value.ContentBinding is not null
            || (read.Value.DatasetGeneration is null) == target.SensitivityLabel.IsPresent
            || !CryptographicOperations.FixedTimeEquals(read.Value.RequestDigest, requestDigest))
        {
            return MemoryErasureProtocol.InvalidPreflightError;
        }

        MemoryErasurePlanTokenFacts plan = read.Value;

        Result<ErasureApplied> applied;

        CovenantWriteLease? lease = null;

        try
        {
            CovenantArtifactErasureAuthority? labelAuthority = null;

            // A labelled entry takes the one write lease over its own scope before BEGIN, and the lease
            // must still see the dataset the label was measured in. An entry already gone takes no
            // lease: the transaction answers it, replaying a receipt that committed meanwhile, refusing
            // an erased subject with 410 and a missing entry with 404.
            if (plan.DatasetGeneration is { } generation
                && await ErasureEntryExistsAsync(connection, target.EntryId, cancellationToken).ConfigureAwait(false))
            {
                Result<CovenantWriteLease> acquired = await dependencies.Gate
                    .AcquireWriteAsync(ErasureOwnerOf(target.Scope), cancellationToken)
                    .ConfigureAwait(false);

                if (acquired.IsFailure)
                {
                    return acquired.Error;
                }

                lease = acquired.Value;

                Result<CovenantArtifactErasureAuthority> borrowed =
                    CovenantArtifactErasureAuthority.ForOrdinary(lease, authority, dependencies.Issuer);

                if (borrowed.IsFailure)
                {
                    return borrowed.Error;
                }

                labelAuthority = borrowed.Value;

                if (lease.Snapshot.DatasetGeneration != generation)
                {
                    return ErasureStalePlan;
                }

                Result held = await ValidateCurationLeaseAsync(lease, target.Scope, cancellationToken).ConfigureAwait(false);

                if (held.IsFailure)
                {
                    return held.Error;
                }
            }

            ErasureApplication application = new(key, target, mutationId, requestDigest, plan, lease, labelAuthority, dependencies.Initializer);

            try
            {
                applied = await SqliteBusyRetry.ExecuteAsync(
                    () => ApplyErasureInTransactionAsync(connection, application, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ErasureUncertainCommitException uncertain)
            {
                // The transaction is closed, so a commit that did not persist has been rolled back. Only
                // the receipt can say which happened.
                applied = await SettleUncertainErasureCommitAsync(dependencies.Scrubber, mutationId, requestDigest, uncertain)
                    .ConfigureAwait(false);
            }
            catch (InspectionException exception)
            {
                applied = exception.Error;
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

        // Reached only after a successful COMMIT, a receipt read back after an uncertain one, or a
        // receipt the transaction found already committed: the transaction and the lease are both
        // closed, and a refused erase returned above.
        return await MemoryErasureProtocol
            .FinishAsync(connection, dependencies.Scrubber, applied.Value.Receipt, applied.Value.Replayed, notes, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The prepare snapshot: the entry must be live and exactly the target, and then its plan is
    /// measured.
    /// </summary>
    private async Task<Result<ErasureMeasurement>> MeasurePreparedErasureAsync(
        SqliteConnection connection,
        MemoryErasureKey key,
        LexiconCurationTarget target,
        CancellationToken cancellationToken)
    {
        if (!await ErasureEntryExistsAsync(connection, target.EntryId, cancellationToken).ConfigureAwait(false))
        {
            return await MemoryErasureEvidence
                .SubjectErasedAsync(connection, null, ErasureSubject(key, target), cancellationToken)
                .ConfigureAwait(false)
                ? ErasureSubjectErased
                : ErasureEntryNotFound;
        }

        CurationState state = await ReadCurationStateAsync(connection, target, cancellationToken).ConfigureAwait(false);

        return TargetsEqual(target, state.Detail.Target)
            ? await MeasureErasureAsync(connection, target, state, cancellationToken).ConfigureAwait(false)
            : StaleTargetError;
    }

    /// <summary>
    /// The erase itself: one raw <c>BEGIN IMMEDIATE</c> that re-proves everything the plan assumed,
    /// removes the entry, records the evidence, proves absence, and commits.
    /// </summary>
    private async Task<Result<ErasureApplied>> ApplyErasureInTransactionAsync(
        SqliteConnection connection,
        ErasureApplication application,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

        Result<ErasureApplied> applied;

        try
        {
            applied = await EraseWithinTransactionAsync(connection, application, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TryRollbackAsync(connection, "erasure").ConfigureAwait(false);

            throw;
        }

        if (applied.IsFailure || applied.Value.Replayed)
        {
            // A refusal, or a receipt another apply already committed: nothing this transaction did may
            // stand.
            await TryRollbackAsync(connection, "erasure").ConfigureAwait(false);

            return applied;
        }

        try
        {
            await (ErasureCommitForTesting is { } commit
                ? commit(connection, cancellationToken)
                : ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT")).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException && !IsErasureCommitBusy(failure))
        {
            // A busy COMMIT persisted nothing, and the retry's first step re-probes the receipt. Anything
            // else may have persisted the frame before it failed, so the outcome is uncertain.
            await RollbackOpenErasureAsync(connection).ConfigureAwait(false);

            throw new ErasureUncertainCommitException(failure);
        }
        catch
        {
            await RollbackOpenErasureAsync(connection).ConfigureAwait(false);

            throw;
        }

        return applied;
    }

    private async Task<Result<ErasureApplied>> EraseWithinTransactionAsync(
        SqliteConnection connection,
        ErasureApplication application,
        CancellationToken cancellationToken)
    {
        LexiconCurationTarget target = application.Target;

        MemoryErasureKey key = application.Key;

        // A concurrent identical apply may have committed while this one waited for the write lock.
        Result<MemoryErasureReceiptRow?> recorded = await MemoryErasureProtocol
            .ProbeReceiptAsync(connection, null, MemoryReviewStore.Lexicon, application.MutationId, application.RequestDigest, cancellationToken)
            .ConfigureAwait(false);

        if (recorded.IsFailure)
        {
            return recorded.Error;
        }

        if (recorded.Value is { } committed)
        {
            return new ErasureApplied(committed, Replayed: true);
        }

        if (await MemoryErasureEvidence
                .SubjectErasedAsync(connection, null, ErasureSubject(key, target), cancellationToken)
                .ConfigureAwait(false))
        {
            return ErasureSubjectErased;
        }

        if (!await ErasureEntryExistsAsync(connection, target.EntryId, cancellationToken).ConfigureAwait(false))
        {
            return ErasureEntryNotFound;
        }

        bool fullTextVerified = await EnsureFullTextSecureDeleteAsync(connection, cancellationToken).ConfigureAwait(false);

        CurationState state = await ReadCurationStateAsync(connection, target, cancellationToken).ConfigureAwait(false);

        if (!TargetsEqual(target, state.Detail.Target))
        {
            return StaleTargetError;
        }

        Result<ErasureMeasurement> measured = await MeasureErasureAsync(connection, target, state, cancellationToken).ConfigureAwait(false);

        if (measured.IsFailure)
        {
            return measured.Error;
        }

        ErasureMeasurement erasing = measured.Value;

        // The label proof: an entry erased without a lease may remove no label, and one erased under a
        // lease removes only a label that lease covers.
        if (application.LabelAuthority is null && erasing.Labels.Count > 0)
        {
            return ErasureStalePlan;
        }

        foreach (MemoryErasureLabelRow label in erasing.Labels)
        {
            if (application.LabelAuthority is { } held && !held.Covers(ErasureOwnerOf(label)))
            {
                return ErasureUncoveredLabel;
            }
        }

        byte[] effectDigest = key.Effect(erasing.Facts);

        if (!CryptographicOperations.FixedTimeEquals(effectDigest, application.Plan.EffectDigest))
        {
            return ErasureStalePlan;
        }

        // Read before the delete: the entry's full-text row is keyed by this rowid, and it is removed
        // only by the content table's delete trigger, so the absence proof has to look for it by key.
        if (await ReadErasureRowIdAsync(connection, target.EntryId, cancellationToken).ConfigureAwait(false) is not { } rowId)
        {
            return ErasureEntryNotFound;
        }

        _ = await CovenantArtifactPlanRunner
            .RunAsync(connection, null, SensitiveArtifactKind.Lexicon, CovenantIdentitySql.Key(target.EntryId), CovenantArtifactPlanMode.Delete, cancellationToken)
            .ConfigureAwait(false);

        // The rows removed are the rows just measured, which the absence proof below shows are gone.
        // The delete's own tally cannot say it: a corrected claim's later versions go by their
        // predecessor's cascade, and SQLite does not count a cascaded row as the statement's change.
        long removedRows = erasing.Facts.Targets.Sum(static table => table.Rows);

        int removedLabels = 0;

        foreach (MemoryErasureLabelRow label in erasing.Labels)
        {
            removedLabels += await MemoryErasureLabels
                .DeleteExactAsync(connection, null, application.Initializer, label.LabelId, label.ArtifactId, cancellationToken)
                .ConfigureAwait(false);
        }

        // From the row's own name under the current rule, never from its stored normalized spelling, so
        // the identity is the one the scribe chokepoint derives from an incoming name.
        _ = await MemoryErasureEvidence
            .InsertFingerprintAsync(
                connection,
                null,
                key.Fingerprint(MemoryErasureIdentity.ForLexicon(target.Scope.CampaignId, state.Row.Entry.Name)),
                MemoryReviewStore.Lexicon,
                key.KeyId.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<MemoryExternalEvidence> evidence = erasing.Facts.Evidence;

        MemoryErasureReceiptRow receipt = new(
            application.MutationId,
            MemoryReviewStore.Lexicon,
            key.KeyId.ToArray(),
            application.RequestDigest,
            effectDigest,
            ErasedItemCount: 1,
            removedRows,
            removedLabels,
            RemovedRetirementSuppressionCount: 0,
            evidence[0],
            evidence[1],
            evidence[2],
            evidence[3],
            evidence[4],
            erasing.Facts.RetainedCopiesMask,
            ErasureScrubPending,
            MemoryErasureScrubPendingReasons.ToMask(
                fullTextVerified
                    ? [MemoryErasureScrubPendingReason.WalCheckpointPending]
                    : [MemoryErasureScrubPendingReason.WalCheckpointPending, MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified]));

        await MemoryErasureEvidence
            .InsertReceiptAsync(connection, null, receipt, [ErasureSubject(key, target)], cancellationToken)
            .ConfigureAwait(false);

        if (!await ProveErasureAbsentAsync(connection, target.EntryId, rowId, cancellationToken).ConfigureAwait(false))
        {
            return ErasureNotAbsent;
        }

        Result lease = await ValidateCurationLeaseAsync(application.Lease, target.Scope, cancellationToken).ConfigureAwait(false);

        if (lease.IsFailure)
        {
            return lease.Error;
        }

        if (application.LabelAuthority is { } labelAuthority)
        {
            Result current = await labelAuthority.RevalidateAsync(cancellationToken).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }
        }

        return new ErasureApplied(receipt, Replayed: false);
    }

    /// <summary>
    /// Measures everything the effect digest binds for one entry, in the caller's snapshot.
    /// </summary>
    private async Task<Result<ErasureMeasurement>> MeasureErasureAsync(
        SqliteConnection connection,
        LexiconCurationTarget target,
        CurationState state,
        CancellationToken cancellationToken)
    {
        CovenantArtifactPlanTally tally = await CovenantArtifactPlanRunner
            .RunAsync(connection, null, SensitiveArtifactKind.Lexicon, CovenantIdentitySql.Key(target.EntryId), CovenantArtifactPlanMode.Count, cancellationToken)
            .ConfigureAwait(false);

        // The entry's normalised id names exactly its own row. A second row under it would be deleted
        // with the entry.
        if (tally.ArtifactRows != 1)
        {
            return ErasureSharedEntryId;
        }

        IReadOnlyList<MemoryErasureLabelRow> labels = await MemoryErasureLabels
            .ReadAsync(connection, null, SensitiveArtifactKind.Lexicon, [target.EntryId.ToString()], cancellationToken)
            .ConfigureAwait(false);

        bool resurfaces = target.Scope.Kind == LexiconScopeKind.Campaign
            && await GlobalEntryExistsAsync(connection, target.NormalizedName, cancellationToken).ConfigureAwait(false);

        MemoryErasureExternalExposureDto exposure = await MemoryErasureExposure
            .ReadLexiconAsync(connection, null, target.EntryId, target.NormalizedName, target.Scope.CampaignId, cancellationToken)
            .ConfigureAwait(false);

        MemoryRetainedLocalCopy[] retained = MemoryErasureRetainedCopies.For(
            MemoryReviewStore.Lexicon,
            MemoryErasureRetainedCopies.AuditFilesExist(options.CurrentValue));

        bool pinned = state.Row.Entry.PinnedAtUtc is not null;

        LexiconCurationAnnalHead head = state.Detail.Target.AnnalHead;

        MemoryErasureEffectFlags flags = (pinned ? MemoryErasureEffectFlags.Pinned : MemoryErasureEffectFlags.None)
            | (resurfaces ? MemoryErasureEffectFlags.GlobalEntryResurfaces : MemoryErasureEffectFlags.None);

        MemoryErasureEffectFacts facts = new(
            MemoryReviewStore.Lexicon,
            [target.EntryId.ToString()],
            [head.IsPresent ? Guid.Parse(head.VersionId!, CultureInfo.InvariantCulture) : null],
            tally.Targets,
            labels.Count,
            RetirementSuppressions: 0,
            flags,
            Covenant: null,
            MemoryErasureExposure.EvidenceCodes(exposure),
            MemoryRetainedLocalCopies.ToMask(retained));

        return new ErasureMeasurement(labels, resurfaces, pinned, exposure, retained, facts);
    }

    /// <summary>
    /// The authoritative absence proof: every planned row counted again, inside the erase's own
    /// transaction, with the predicates that deleted it, no label left, and no full-text row left for
    /// the entry.
    /// </summary>
    /// <remarks>
    /// The full-text row holds the entry's tokens, so it is a planned row too, even though no plan
    /// statement deletes it: the content table's delete trigger does. FTS5 keeps one
    /// <c>lexicon_fts_docsize</c> row for every row it indexes, keyed by the content rowid, so that row's
    /// absence is the exact and cheap proof that the index no longer holds the entry. A retired entry
    /// has none to begin with. An index that has fallen out of step with its rows fails here, and the
    /// erase rolls back rather than reporting an erase the index contradicts.
    /// </remarks>
    private static async Task<bool> ProveErasureAbsentAsync(
        SqliteConnection connection,
        Guid entryId,
        long rowId,
        CancellationToken cancellationToken)
    {
        if (await FullTextRowCountAsync(connection, rowId, cancellationToken).ConfigureAwait(false) != 0)
        {
            return false;
        }

        CovenantArtifactPlanTally left = await CovenantArtifactPlanRunner
            .RunAsync(connection, null, SensitiveArtifactKind.Lexicon, CovenantIdentitySql.Key(entryId), CovenantArtifactPlanMode.Count, cancellationToken)
            .ConfigureAwait(false);

        if (left.Targets.Any(static table => table.Rows != 0))
        {
            return false;
        }

        IReadOnlyList<MemoryErasureLabelRow> labels = await MemoryErasureLabels
            .ReadAsync(connection, null, SensitiveArtifactKind.Lexicon, [entryId.ToString()], cancellationToken)
            .ConfigureAwait(false);

        return labels.Count == 0;
    }

    /// <summary>
    /// Requires the full-text index to scrub what it deletes before anything is deleted, and reports
    /// whether it could be shown to.
    /// </summary>
    /// <remarks>
    /// Version 13 turned secure delete on and merged the index once. An index found with it off has
    /// deleted rows since then without scrubbing them, so the erase turns it back on and merges the
    /// residue away with <c>optimize</c> inside its own transaction, then reads the setting back. A
    /// setting that still does not read 1 is recorded on the receipt as a verdict that never clears.
    /// </remarks>
    private static async Task<bool> EnsureFullTextSecureDeleteAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await FullTextSecureDeleteIsOnAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await ExecuteNonQueryAsync(
            connection,
            cancellationToken,
            "INSERT INTO lexicon_fts(lexicon_fts, rank) VALUES('secure-delete', 1);").ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            cancellationToken,
            "INSERT INTO lexicon_fts(lexicon_fts) VALUES('optimize');").ConfigureAwait(false);

        return await FullTextSecureDeleteIsOnAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The index's applied secure-delete setting, from the one place FTS5 exposes it.</summary>
    private static async Task<bool> FullTextSecureDeleteIsOnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT v FROM lexicon_fts_config WHERE k = 'secure-delete';";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not null and not DBNull && Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>The content rowid of the one row holding the entry's id, or null when none does.</summary>
    private static async Task<long?> ReadErasureRowIdAsync(
        SqliteConnection connection,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT rowid FROM lexicon_entries
            WHERE {CovenantIdentitySql.Keyed("Id", "$id")};
            """;

        _ = command.Parameters.AddWithValue("$id", CovenantIdentitySql.Key(entryId));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rowId ? rowId : null;
    }

    /// <summary>How many rows the full-text index still sizes for one content rowid: one while it is indexed.</summary>
    private static async Task<long> FullTextRowCountAsync(
        SqliteConnection connection,
        long rowId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM lexicon_fts_docsize WHERE id = $rowid;";

        _ = command.Parameters.AddWithValue("$rowid", rowId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rows
            ? rows
            : throw new InvalidDataException("The full-text absence count did not return an integer.");
    }

    /// <summary>Whether a row still holds the entry's id, in any spelling the plan would delete.</summary>
    private static async Task<bool> ErasureEntryExistsAsync(
        SqliteConnection connection,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT count(*) FROM lexicon_entries
            WHERE {CovenantIdentitySql.Keyed("Id", "$id")};
            """;

        _ = command.Parameters.AddWithValue("$id", CovenantIdentitySql.Key(entryId));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rows && rows > 0;
    }

    /// <summary>
    /// Whether a Global entry holds the same normalized name, which a Campaign turn would see once the
    /// Campaign entry is gone.
    /// </summary>
    private static async Task<bool> GlobalEntryExistsAsync(
        SqliteConnection connection,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        // The empty string, not NULL, is the Global scope.
        command.CommandText =
            """
            SELECT count(*) FROM lexicon_entries
            WHERE ScopeCampaignId = '' AND NameNormalized = $name;
            """;

        _ = command.Parameters.AddWithValue("$name", normalizedName);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rows && rows > 0;
    }

    /// <summary>
    /// Settles a commit that failed: the receipt read back on a fresh connection is an erase that
    /// happened, and its absence, or a read that cannot be made, reports the commit's own failure.
    /// </summary>
    private static async Task<Result<ErasureApplied>> SettleUncertainErasureCommitAsync(
        MemoryErasureScrubber scrubber,
        Guid mutationId,
        byte[] requestDigest,
        ErasureUncertainCommitException uncertain)
    {
        Result<MemoryErasureReceiptRow?> reread = await scrubber
            .ReadCommittedReceiptAsync(mutationId, CancellationToken.None)
            .ConfigureAwait(false);

        if (reread.IsSuccess
            && reread.Value is { Store: MemoryReviewStore.Lexicon } persisted
            && CryptographicOperations.FixedTimeEquals(persisted.RequestDigest, requestDigest))
        {
            return new ErasureApplied(persisted, Replayed: false);
        }

        ExceptionDispatchInfo.Capture(uncertain.InnerException!).Throw();

        throw new UnreachableException();
    }

    /// <summary>Rolls back only a transaction that is still open, as a failed commit may or may not leave one.</summary>
    private async Task RollbackOpenErasureAsync(SqliteConnection connection)
    {
        if (connection.State == ConnectionState.Open && SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0)
        {
            await TryRollbackAsync(connection, "erasure").ConfigureAwait(false);
        }
    }

    /// <summary>Whether a failure is SQLite's busy or locked answer, which the busy retry handles.</summary>
    private static bool IsErasureCommitBusy(Exception failure)
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
    /// The erase's own shape checks, before anything is opened: a complete target whose head version is
    /// a GUID, a name that is not a daemon's, and a mutation id.
    /// </summary>
    private static Result<LexiconCurationTarget> CheckErasureTarget(LexiconCurationTarget? target, Guid? mutationId)
    {
        if (target is null
            || target.Validate().IsFailure
            || (target.AnnalHead.IsPresent && !Guid.TryParse(target.AnnalHead.VersionId, CultureInfo.InvariantCulture, out _)))
        {
            return ErasureInvalidTarget;
        }

        if (LexiconDaemonStateNames.Is(target.NormalizedName))
        {
            return DaemonStateErasureRefused;
        }

        return mutationId is { } id && id != Guid.Empty
            ? Result<LexiconCurationTarget>.Success(target)
            : Result<LexiconCurationTarget>.Failure(ErasureInvalidBody);
    }

    /// <summary>The notes an erase in this exact scope states, before and after it applies.</summary>
    private static MemoryErasureNote[] ErasureNotes(LexiconCurationTarget target) =>
        MemoryErasureNotes.For(
            MemoryReviewStore.Lexicon,
            MemoryErasureIdentity.ForLexicon(target.Scope.CampaignId, target.NormalizedName).Scope,
            reclaimsKey: false);

    private static byte[] ErasureSubject(MemoryErasureKey key, LexiconCurationTarget target) =>
        key.Subject(MemoryReviewStore.Lexicon, target.EntryId.ToString());

    private static CovenantOperationScope ErasureOwnerOf(LexiconCurationScope scope) =>
        scope.Kind == LexiconScopeKind.Global
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(scope.CampaignId!.Value);

    private static CovenantOperationScope ErasureOwnerOf(MemoryErasureLabelRow label) =>
        label.OwnerCampaignId is { } campaign
            ? CovenantOperationScope.ForCampaign(campaign)
            : CovenantOperationScope.Global;

    /// <summary>Everything one apply carries into its transaction, and into a busy retry of it.</summary>
    private sealed record ErasureApplication(
        MemoryErasureKey Key,
        LexiconCurationTarget Target,
        Guid MutationId,
        byte[] RequestDigest,
        MemoryErasurePlanTokenFacts Plan,
        CovenantWriteLease? Lease,
        CovenantArtifactErasureAuthority? LabelAuthority,
        ICovenantSqliteConnectionInitializer Initializer);

    private sealed record ErasureMeasurement(
        IReadOnlyList<MemoryErasureLabelRow> Labels,
        bool GlobalEntryResurfaces,
        bool Pinned,
        MemoryErasureExternalExposureDto Exposure,
        MemoryRetainedLocalCopy[] Retained,
        MemoryErasureEffectFacts Facts);

    private sealed record ErasureApplied(MemoryErasureReceiptRow Receipt, bool Replayed);

    /// <summary>A <c>COMMIT</c> that failed in a way that may still have persisted.</summary>
    private sealed class ErasureUncertainCommitException(Exception commitFailure)
        : Exception("The erase's commit failed, and its outcome is settled by its receipt.", commitFailure);
}
