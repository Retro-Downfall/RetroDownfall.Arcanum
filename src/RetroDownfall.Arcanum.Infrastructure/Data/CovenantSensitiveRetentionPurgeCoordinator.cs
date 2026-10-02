using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Issue #117 — the one narrow boundary a direct-deletion route dispatches a potentially labelled
/// artifact through.
/// </summary>
/// <remarks>
/// Everything this type knows about deleting an artifact it learns from
/// <see cref="CovenantSensitiveArtifactPurgePolicy"/> and performs through the two shared kernels. It
/// deliberately implements no second purge, capability-open, ownership-verification, compare-delete,
/// fsync, or label-removal algorithm, holds no capability opener, ownership verifier, or operation
/// gate for those purposes, and creates no enum value, numeric mapping, policy switch, or second
/// registry. Three erasure paths deleting the same artifacts through three switch statements is how one
/// of them ends up preserving evidence the other two remove (§10.17).
///
/// <para>It acquires exactly one <c>CovenantWriteLease</c>, over the single owner scope the labels
/// themselves name, holds it for the whole bounded purge, and disposes it once afterwards. It never
/// acquires or carries a connection authorization: each kernel borrows and disposes its own SQL
/// authorization on its own live transaction.</para>
///
/// <para>Labelled targets are dispatched one at a time, database-owned ones first and managed files
/// after, and each is classified from its own erasure progress rather than from a page total.
/// <see cref="CovenantSensitivePurgeDisposition.Purged"/> means the kernel erased that item and nothing
/// else does. Once an item is blocked, every later item is recorded blocked with the same blocker and
/// is never dispatched. An item the kernel examined but did not erase has its label read again while
/// the lease is still held: an absent label leaves it unlabelled for the caller's ordinary delete, and
/// a present one means the label moved after it was resolved, so it is blocked as stale (§10.20.2).</para>
///
/// <para>An installation with no Covenant arm, or a batch in which nothing carries a label, answers
/// every target <see cref="CovenantSensitivePurgeDisposition.Unlabeled"/> and acquires nothing at all,
/// so a route that predates this boundary behaves byte-for-byte as it did.</para>
/// </remarks>
internal sealed class CovenantSensitiveRetentionPurgeCoordinator(
    IArtifactSensitivityLedger labels,
    ICovenantConnectionSource connections,
    CovenantManagedFileErasureRequestReader managedFileRequests,
    ICovenantOperationGate gate,
    IOperatorAuthorityContextIssuer issuer,
    ICovenantAvailability availability,
    ICovenantProtectedArtifactErasureKernel artifacts,
    ICovenantManagedFileErasureKernel managedFiles,
    CovenantSensitivePurgeAuthorityScope authorityScope) : ICovenantSensitiveArtifactPurger
{

    public async ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count is 0 or > ICovenantSensitiveArtifactPurger.MaxTargets)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                $"A sensitivity purge carries between 1 and {ICovenantSensitiveArtifactPurger.MaxTargets} artifacts.");

        }

        // Duplicates would be examined twice, and the second look would find the row already gone
        // without being able to tell that from a row somebody else removed.
        if (targets.Select(static target => target.ArtifactId).Distinct().Count() != targets.Count)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A sensitivity purge cannot name the same artifact twice.");

        }

        Result<List<LabeledTarget>> resolved = await ResolveLabelsAsync(targets, cancellationToken)
            .ConfigureAwait(false);

        if (resolved.IsFailure)
        {

            return resolved.Error;

        }

        List<LabeledTarget> labeled = resolved.Value;

        if (labeled.Count == 0)
        {

            return Unlabeled(targets);

        }

        // A labelled artifact is the only thing that needs authority, so the check happens here rather
        // than at entry. Requiring it unconditionally would make every ordinary Saga delete on an
        // installation with the feature off fail for want of a context nothing ever issues.
        if (authorityScope.Current is not { } operatorContext)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "Deleting a labelled artifact requires operator authority issued for a sensitivity retention purge.");

        }

        Result<CovenantOperationScope> owner = ResolveSingleOwner(labeled);

        if (owner.IsFailure)
        {

            return owner.Error;

        }

        Result<CovenantWriteLease> lease = await gate
            .AcquireWriteAsync(owner.Value, cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsFailure)
        {

            return lease.Error;

        }

        await using CovenantWriteLease held = lease.Value;

        Result<CovenantArtifactErasureAuthority> authority =
            CovenantArtifactErasureAuthority.ForOrdinary(held, operatorContext, issuer);

        if (authority.IsFailure)
        {

            return authority.Error;

        }

        return await ExecuteAsync(
            targets,
            labeled,
            authority.Value,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Reads each target's live label, keeping only the ones that carry one.
    /// </summary>
    /// <remarks>
    /// An installation with the Covenant off still has the core label table, so its targets read as
    /// unlabelled and ordinary deletion is unaffected.
    ///
    /// <para>A label that cannot be read at all refuses the whole purge with
    /// <c>Covenant.Unavailable</c> before anything is dispatched. Failing open here once returned the
    /// labels read so far, so every remaining target read as unlabelled and the caller deleted it through
    /// its ordinary path whatever its label said.</para>
    /// </remarks>
    private async Task<Result<List<LabeledTarget>>> ResolveLabelsAsync(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets,
        CancellationToken cancellationToken)
    {

        List<LabeledTarget> labeled = [];

        foreach (CovenantSensitivePurgeTarget target in targets)
        {

            Result<ArtifactSensitivityLabel?> label = await ReadLabelAsync(
                target,
                "The sensitivity labels could not be read, so nothing was deleted.",
                cancellationToken).ConfigureAwait(false);

            if (label.IsFailure)
            {

                return label.Error;

            }

            if (label.Value is { } live)
            {

                labeled.Add(new LabeledTarget(target, live));

            }

        }

        return Result<List<LabeledTarget>>.Success(labeled);

    }

    /// <summary>
    /// Reads one target's live label, failing closed when storage cannot answer.
    /// </summary>
    /// <remarks>
    /// Both label reads go through here: the resolution before the lease is used, and the reread that
    /// classifies an item the kernel examined but did not erase. A <see cref="SqliteException"/> in
    /// either is <c>Covenant.Unavailable</c>, because "could not read" is not "no label".
    /// </remarks>
    private async Task<Result<ArtifactSensitivityLabel?>> ReadLabelAsync(
        CovenantSensitivePurgeTarget target,
        string unavailableMessage,
        CancellationToken cancellationToken)
    {

        try
        {

            return await labels
                .TryReadLabelAsync(target.Kind, target.ArtifactId, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (SqliteException)
        {

            return new Error(ErrorCodes.Covenant.Unavailable, unavailableMessage);

        }

    }

    /// <summary>
    /// The one owner scope every labelled artifact in this batch belongs to.
    /// </summary>
    /// <remarks>
    /// One lease means one scope. A batch spanning two Campaigns is refused rather than covered by an
    /// installation-wide capability the caller never asked for: a Campaign's rows are not "part of" the
    /// Global scope, and treating them as though they were is how one purge quietly deletes another
    /// Campaign's memory (§10.17).
    /// </remarks>
    private static Result<CovenantOperationScope> ResolveSingleOwner(IReadOnlyList<LabeledTarget> labeled)
    {

        Guid? campaignId = labeled[0].Label.CampaignId;

        foreach (LabeledTarget candidate in labeled)
        {

            if (candidate.Label.CampaignId != campaignId)
            {

                return new Error(
                    ErrorCodes.Covenant.InvalidScope,
                    "A sensitivity purge covers exactly one owner scope; these artifacts span more than one.");

            }

        }

        return Result<CovenantOperationScope>.Success(
            campaignId is { } campaign
                ? CovenantOperationScope.ForCampaign(campaign)
                : CovenantOperationScope.Global);

    }

    /// <summary>
    /// Dispatches each labelled target on its own and records what its own progress says happened.
    /// </summary>
    /// <remarks>
    /// One item per kernel call, because a page total cannot say which of its items were erased: a page
    /// that stopped at its third item reported every item blocked, including the two it had already
    /// removed, and a page whose item had no live label reported it purged although nothing was deleted.
    ///
    /// <para>The first blocked item stops the walk. Every later item is recorded blocked with the same
    /// blocker and is never dispatched or reread, so nothing is examined under an authority that has
    /// already been shown not to hold. Unlabelled targets keep the disposition they were given up
    /// front.</para>
    /// </remarks>
    private async ValueTask<Result<CovenantSensitivePurgeOutcome>> ExecuteAsync(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets,
        IReadOnlyList<LabeledTarget> labeled,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken)
    {

        Dictionary<Guid, CovenantSensitivePurgeResult> results = [];

        foreach (CovenantSensitivePurgeTarget target in targets)
        {

            results[target.ArtifactId] = new CovenantSensitivePurgeResult(
                target.ArtifactId,
                target.Kind,
                CovenantSensitivePurgeDisposition.Unlabeled,
                CovenantErasureBlocker.None);

        }

        CovenantArtifactErasureProgress progress = CovenantArtifactErasureProgress.Empty;

        List<LabeledTarget> databaseOwned = [.. labeled.Where(static candidate =>
            ExecutorOf(candidate) == CovenantArtifactPurgeExecutor.DatabaseTransaction)];

        List<LabeledTarget> managed = [.. labeled.Where(static candidate =>
            ExecutorOf(candidate) == CovenantArtifactPurgeExecutor.ManagedFileKernel)];

        CovenantErasureBlocker? stoppedBy = null;

        foreach (LabeledTarget candidate in databaseOwned.Concat(managed))
        {

            if (stoppedBy is { } blocker)
            {

                Record(results, candidate, CovenantSensitivePurgeDisposition.Blocked, blocker);

                continue;

            }

            Result<CovenantArtifactErasureProgress> step =
                ExecutorOf(candidate) == CovenantArtifactPurgeExecutor.DatabaseTransaction
                    ? await ErasePageAsync([candidate], authority, cancellationToken).ConfigureAwait(false)
                    : await EraseManagedFileAsync(candidate, authority, cancellationToken).ConfigureAwait(false);

            if (step.IsFailure)
            {

                return step.Error;

            }

            progress = progress.Add(step.Value);

            Result<(CovenantSensitivePurgeDisposition Disposition, CovenantErasureBlocker Blocker)> classified =
                await ClassifyAsync(candidate, step.Value, cancellationToken).ConfigureAwait(false);

            if (classified.IsFailure)
            {

                return classified.Error;

            }

            Record(results, candidate, classified.Value.Disposition, classified.Value.Blocker);

            if (classified.Value.Disposition is CovenantSensitivePurgeDisposition.Blocked)
            {

                stoppedBy = classified.Value.Blocker;

            }

        }

        return new CovenantSensitivePurgeOutcome([.. results.Values], progress);

    }

    /// <summary>
    /// What one dispatched item's own progress says happened to it.
    /// </summary>
    /// <remarks>
    /// Blocked is the kernel's own answer. Purged is exactly one erased item, and nothing else is: the
    /// kernel answers "no live label at the identity, kind, revision, and digest this page named" with
    /// one item examined and none erased, which describes an artifact that is still there.
    ///
    /// <para>That item's label is read again to decide which of two things happened. Absent, the label
    /// is gone and the artifact is unprotected, so the caller's ordinary delete is the right one.
    /// Present, the label moved between the first read and the kernel's own, and the artifact is blocked
    /// as stale rather than deleted on the strength of a label that no longer describes it. The reread
    /// runs inside <see cref="ExecuteAsync"/>, so it happens while the write lease is still held, and it
    /// fails closed on a storage error like the first read.</para>
    /// </remarks>
    private async Task<Result<(CovenantSensitivePurgeDisposition Disposition, CovenantErasureBlocker Blocker)>> ClassifyAsync(
        LabeledTarget candidate,
        CovenantArtifactErasureProgress step,
        CancellationToken cancellationToken)
    {

        if (step.IsBlocked)
        {

            return Result<(CovenantSensitivePurgeDisposition, CovenantErasureBlocker)>.Success(
                (CovenantSensitivePurgeDisposition.Blocked, step.Blocker));

        }

        if (step.ErasedCount == 1)
        {

            return Result<(CovenantSensitivePurgeDisposition, CovenantErasureBlocker)>.Success(
                (CovenantSensitivePurgeDisposition.Purged, CovenantErasureBlocker.None));

        }

        Result<ArtifactSensitivityLabel?> reread = await ReadLabelAsync(
            candidate.Target,
            "A sensitivity label could not be read again, so the artifact it names was left unchanged.",
            cancellationToken).ConfigureAwait(false);

        if (reread.IsFailure)
        {

            return reread.Error;

        }

        return Result<(CovenantSensitivePurgeDisposition, CovenantErasureBlocker)>.Success(
            reread.Value is null
                ? (CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None)
                : (CovenantSensitivePurgeDisposition.Blocked, CovenantErasureBlocker.AuthorityStale));

    }

    private async ValueTask<Result<CovenantArtifactErasureProgress>> ErasePageAsync(
        IReadOnlyList<LabeledTarget> databaseOwned,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken)
    {

        if (availability.Current.DatasetGeneration is not { } datasetGeneration
            || datasetGeneration == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The Covenant dataset generation is unavailable, so no erasure page can be computed.");

        }

        List<CovenantProtectedArtifactErasureItem> items = [];

        foreach (LabeledTarget candidate in databaseOwned)
        {

            items.Add(
                new CovenantProtectedArtifactErasureItem(
                    candidate.Target.ArtifactId,
                    candidate.Target.Kind,
                    candidate.Label.SessionId,
                    candidate.Label.LabelId,
                    candidate.Label,
                    candidate.Label.ArtifactContentDigest,
                    candidate.Label.ArtifactRevision));

        }

        return await artifacts
            .ErasePageAsync(
                new CovenantProtectedArtifactErasurePage(datasetGeneration, items),
                authority,
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Delegates one managed workspace file to the single managed-file kernel, exactly once.
    /// </summary>
    /// <remarks>
    /// The inventory read returns identities only — the source managed-write operation, its observed
    /// revision, and any work item this source already has. The kernel rereads the authoritative
    /// location and ownership from the producer's own durable row, because a caller-supplied location
    /// is exactly how a deleter is pointed at a file Arcanum never created (§10.17).
    /// </remarks>
    private async ValueTask<Result<CovenantArtifactErasureProgress>> EraseManagedFileAsync(
        LabeledTarget file,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken)
    {

        Result<CovenantManagedFileErasureIdentity?> source = await ReadManagedSourceAsync(
            file,
            cancellationToken).ConfigureAwait(false);

        if (source.IsFailure)
        {

            return source.Error;

        }

        if (source.Value is not { } identity)
        {

            // A labelled managed file with no adopted producer has no durable row naming the file the
            // kernel would be authorized to remove. Reporting a manual blocker keeps the label and the
            // file exactly as found rather than guessing at a path.
            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(1, 0, 1, CovenantErasureBlocker.ManualOwnershipMismatch));

        }

        return await managedFiles
            .EraseAsync(
                identity.ToRequest(Guid.NewGuid()),
                authority,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<Result<CovenantManagedFileErasureIdentity?>> ReadManagedSourceAsync(
        LabeledTarget file,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await managedFileRequests
            .TryReadWithinAsync(connection, transaction: null, file.Label, cancellationToken)
            .ConfigureAwait(false);

    }

    private static void Record(
        Dictionary<Guid, CovenantSensitivePurgeResult> results,
        LabeledTarget candidate,
        CovenantSensitivePurgeDisposition disposition,
        CovenantErasureBlocker blocker) =>
        results[candidate.Target.ArtifactId] = new CovenantSensitivePurgeResult(
            candidate.Target.ArtifactId,
            candidate.Target.Kind,
            disposition,
            blocker);

    private static CovenantArtifactPurgeExecutor ExecutorOf(LabeledTarget candidate) =>
        CovenantSensitiveArtifactPurgePolicy.Resolve(candidate.Target.Kind).Value.Executor;

    private static CovenantSensitivePurgeOutcome Unlabeled(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets) =>
        new(
            [.. targets.Select(static target => new CovenantSensitivePurgeResult(
                target.ArtifactId,
                target.Kind,
                CovenantSensitivePurgeDisposition.Unlabeled,
                CovenantErasureBlocker.None))],
            CovenantArtifactErasureProgress.Empty);

    private sealed record LabeledTarget(
        CovenantSensitivePurgeTarget Target,
        ArtifactSensitivityLabel Label);

}
