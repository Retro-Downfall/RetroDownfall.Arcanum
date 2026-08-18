using System.Globalization;

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
/// <para>An installation with no Covenant arm, or a batch in which nothing carries a label, answers
/// every target <see cref="CovenantSensitivePurgeDisposition.Unlabeled"/> and acquires nothing at all,
/// so a route that predates this boundary behaves byte-for-byte as it did.</para>
/// </remarks>
internal sealed class CovenantSensitiveRetentionPurgeCoordinator(
    IArtifactSensitivityLedger labels,
    ICovenantConnectionSource connections,
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
    /// A missing Covenant tier reports every target as unlabelled rather than failing: an installation
    /// that never had protected state has nothing for this boundary to protect, and turning that into
    /// an error would break ordinary deletion on every installation with the feature off.
    /// </remarks>
    private async Task<Result<List<LabeledTarget>>> ResolveLabelsAsync(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets,
        CancellationToken cancellationToken)
    {

        List<LabeledTarget> labeled = [];

        foreach (CovenantSensitivePurgeTarget target in targets)
        {

            Result<ArtifactSensitivityLabel?> label;

            try
            {

                label = await labels
                    .TryReadLabelAsync(target.Kind, target.ArtifactId, cancellationToken)
                    .ConfigureAwait(false);

            }
            catch (SqliteException)
            {

                // The core support tables are always present on a healthy installation; a database
                // that cannot answer at all is reported by the availability surface, not turned into a
                // deletion refusal here.
                return Result<List<LabeledTarget>>.Success(labeled);

            }

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
            CovenantSensitiveArtifactPurgePolicy.Resolve(candidate.Target.Kind).Value.Executor
                == CovenantArtifactPurgeExecutor.DatabaseTransaction)];

        List<LabeledTarget> managed = [.. labeled.Where(static candidate =>
            CovenantSensitiveArtifactPurgePolicy.Resolve(candidate.Target.Kind).Value.Executor
                == CovenantArtifactPurgeExecutor.ManagedFileKernel)];

        if (databaseOwned.Count > 0)
        {

            Result<CovenantArtifactErasureProgress> page = await ErasePageAsync(
                databaseOwned,
                authority,
                cancellationToken).ConfigureAwait(false);

            if (page.IsFailure)
            {

                return page.Error;

            }

            progress = progress.Add(page.Value);

            Record(results, databaseOwned, page.Value.Blocker);

        }

        foreach (LabeledTarget file in managed)
        {

            Result<CovenantArtifactErasureProgress> erased = await EraseManagedFileAsync(
                file,
                authority,
                cancellationToken).ConfigureAwait(false);

            if (erased.IsFailure)
            {

                return erased.Error;

            }

            progress = progress.Add(erased.Value);

            Record(results, [file], erased.Value.Blocker);

        }

        return new CovenantSensitivePurgeOutcome([.. results.Values], progress);

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

        Result<ManagedFileSourceIdentity?> source = await ReadManagedSourceAsync(
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
                new CovenantManagedFileErasureRequest(
                    identity.ExistingWorkItemId ?? Guid.NewGuid(),
                    Guid.NewGuid(),
                    identity.SourceWriteOperationId,
                    file.Target.ArtifactId,
                    file.Label.LabelId,
                    identity.ObservedSourceRevision),
                authority,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<Result<ManagedFileSourceIdentity?>> ReadManagedSourceAsync(
        LabeledTarget file,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        // Phase 7 is AdoptedAndLabeled: the only phase at which a producer both owns a real file and
        // has handed its label to the erasure side. Any other phase is the write intent's own recovery
        // to finish, not this purge's to pre-empt.
        command.CommandText = """
            SELECT i.WriteOperationId,
                   i.Revision,
                   (SELECT w.WorkItemId
                    FROM local_erasure_work_items w
                    WHERE w.SourceWriteOperationId = i.WriteOperationId
                      AND w.StateCode IN (1, 2)
                    LIMIT 1)
            FROM managed_file_write_intents i
            WHERE i.ArtifactId = $artifactId
              AND i.SensitivityLabelId = $labelId
              AND i.PhaseCode = 7
            LIMIT 1;
            """;

        // Uppercase, and not by accident. Every Covenant identity column stores the uppercase "D" form
        // and SQLite's default TEXT collation is BINARY, so a lowercase bind matches nothing — which
        // here would report every managed file as having no adopted producer and quietly turn each one
        // into a manual blocker instead of erasing it.
        _ = command.Parameters.AddWithValue("$artifactId", Format(file.Target.ArtifactId));

        _ = command.Parameters.AddWithValue("$labelId", Format(file.Label.LabelId));

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<ManagedFileSourceIdentity?>.Success(null);

        }

        return Result<ManagedFileSourceIdentity?>.Success(
            new ManagedFileSourceIdentity(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                checked((ulong)reader.GetInt64(1)),
                reader.IsDBNull(2)
                    ? null
                    : Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));

    }

    private static void Record(
        Dictionary<Guid, CovenantSensitivePurgeResult> results,
        IReadOnlyList<LabeledTarget> handled,
        CovenantErasureBlocker blocker)
    {

        foreach (LabeledTarget candidate in handled)
        {

            results[candidate.Target.ArtifactId] = new CovenantSensitivePurgeResult(
                candidate.Target.ArtifactId,
                candidate.Target.Kind,
                blocker is CovenantErasureBlocker.None
                    ? CovenantSensitivePurgeDisposition.Purged
                    : CovenantSensitivePurgeDisposition.Blocked,
                blocker);

        }

    }

    private static CovenantSensitivePurgeOutcome Unlabeled(
        IReadOnlyList<CovenantSensitivePurgeTarget> targets) =>
        new(
            [.. targets.Select(static target => new CovenantSensitivePurgeResult(
                target.ArtifactId,
                target.Kind,
                CovenantSensitivePurgeDisposition.Unlabeled,
                CovenantErasureBlocker.None))],
            CovenantArtifactErasureProgress.Empty);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private sealed record LabeledTarget(
        CovenantSensitivePurgeTarget Target,
        ArtifactSensitivityLabel Label);

    private sealed record ManagedFileSourceIdentity(
        Guid SourceWriteOperationId,
        ulong ObservedSourceRevision,
        Guid? ExistingWorkItemId);

}
