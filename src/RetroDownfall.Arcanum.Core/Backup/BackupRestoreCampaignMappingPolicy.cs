using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Backup;

/// <summary>
/// The sole authority on what a restore's explicit source-to-destination Campaign mappings may say.
/// </summary>
/// <remarks>
/// Pure, and in Core, for the same reason <see cref="BackupRestoreProtectedStatePolicy"/> is: the plan a
/// rehearsal returns, the restore that executes it, and the operator surface that has to explain a
/// refusal before anything exists all have to reach the same decision. Two implementations that agreed
/// on the day they were written would, at their first divergence, let a dry run promise an import the
/// real one refuses.
///
/// <para>The two arms are the two things that can be known at different costs.
/// <see cref="EvaluateShape"/> needs nothing but the request, so it answers before the archive is
/// opened and before the live Grimoire is read at all. <see cref="EvaluateDestination"/> needs the
/// destination's own Campaign identities, which cost a second read-only open and therefore a key
/// derivation — <see cref="RequiresDestinationCampaigns"/> exists so that read is paid for only by a
/// restore whose mappings could still be honoured (§10.19.12).</para>
/// </remarks>
public static class BackupRestoreCampaignMappingPolicy
{

    /// <summary>A Campaign mapping was supplied for a restore that imports no Sessions.</summary>
    public const string NotApplicableCode = "backup.restore_campaign_mapping_not_applicable";

    /// <summary>A Campaign mapping was supplied on an installation with no Covenant import arm.</summary>
    public const string CovenantRequiredCode = "backup.restore_campaign_mapping_covenant_required";

    /// <summary>A Campaign mapping named no archived source, or no destination, or neither.</summary>
    public const string IncompleteCode = "backup.restore_campaign_mapping_incomplete";

    /// <summary>One archived Campaign was mapped to two different destinations.</summary>
    public const string DuplicateSourceCode = "backup.restore_campaign_mapping_duplicate_source";

    /// <summary>A mapping named a destination Campaign this installation does not have.</summary>
    public const string DestinationMissingCode = "backup.restore_campaign_mapping_destination_missing";

    private const string Unmodified = "The current installation was not modified.";

    /// <summary>
    /// Whether this request's mappings still need the destination's Campaign identities to be decided.
    /// </summary>
    /// <param name="request">The restore as the operator asked for it.</param>
    /// <param name="covenantImportArmActive">
    /// Whether this installation routes a selective import through the protected transfer store.
    /// </param>
    /// <remarks>
    /// False for every restore that names no mapping, for every conflict mode that cannot carry one, and
    /// for every request the shape arm already refuses, so the ordinary restore path never pays for the
    /// extra read-only open — and therefore the extra key derivation — that a mapping check needs.
    /// </remarks>
    public static bool RequiresDestinationCampaigns(
        BackupRestoreRequest request,
        bool covenantImportArmActive)
    {

        ArgumentNullException.ThrowIfNull(request);

        return request.CampaignMappings is { Length: > 0 }
            && EvaluateShape(request, covenantImportArmActive).Count == 0;

    }

    /// <summary>
    /// Decides everything about the mappings that can be decided from the request alone.
    /// </summary>
    /// <param name="request">The restore as the operator asked for it.</param>
    /// <param name="covenantImportArmActive">
    /// Whether this installation routes a selective import through the protected transfer store — the
    /// only path that honours a Campaign mapping at all.
    /// </param>
    public static IReadOnlyList<BackupVerifyIssue> EvaluateShape(
        BackupRestoreRequest request,
        bool covenantImportArmActive)
    {

        ArgumentNullException.ThrowIfNull(request);

        BackupSessionCampaignMapping[] mappings = request.CampaignMappings ?? [];

        if (mappings.Length == 0)
        {

            return [];

        }

        if (request.ConflictMode is not BackupRestoreConflictMode.ImportSelectedSessions)
        {

            // Applicability first, and once, however many mappings were supplied. A replacement adopts
            // the archive's own Campaigns wholesale and a new profile root has none of this machine's,
            // so there is nothing for a source-to-destination mapping to be a mapping between.
            return
            [
                new BackupVerifyIssue(
                    NotApplicableCode,
                    "An explicit Campaign mapping applies only to a selective Session import. Re-run "
                    + "without --map-campaign, or with --conflict-mode import-selected-sessions. "
                    + Unmodified),
            ];

        }

        if (!covenantImportArmActive)
        {

            // Refused rather than accepted and ignored. The pre-Covenant import writes every Session's
            // CampaignId as NULL, so honouring the flag here is impossible and accepting it would hand
            // the operator exactly the silently unbound Session the mapping exists to prevent — an
            // unbuilt capability must not look like a built one that happens to be quiet.
            return
            [
                new BackupVerifyIssue(
                    CovenantRequiredCode,
                    "This installation does not run the Covenant selective-import arm, so a Campaign "
                    + "mapping would authorize nothing and every imported Session would arrive unbound. "
                    + "Enable Arcanum:Features:Covenant before importing with an explicit Campaign "
                    + "mapping. "
                    + Unmodified),
            ];

        }

        List<BackupVerifyIssue> issues = [];

        foreach (BackupSessionCampaignMapping mapping in mappings)
        {

            if (mapping.SourceCampaignId == Guid.Empty || mapping.DestinationCampaignId == Guid.Empty)
            {

                issues.Add(new BackupVerifyIssue(
                    IncompleteCode,
                    "A Campaign mapping needs an archived source Campaign and a destination Campaign on "
                    + $"this machine; --map-campaign {mapping.SourceCampaignId:D}="
                    + $"{mapping.DestinationCampaignId:D} names the nil identity on one side. "
                    + Unmodified));

            }

        }

        foreach (IGrouping<Guid, BackupSessionCampaignMapping> group in mappings
                     .Where(static mapping => mapping.SourceCampaignId != Guid.Empty)
                     .GroupBy(static mapping => mapping.SourceCampaignId))
        {

            // Repeating the identical mapping is one unambiguous instruction stated twice; two
            // destinations for one archived Campaign is a question only the operator can answer, and
            // picking either would bind an imported Session to a Campaign they did not choose.
            if (group.Select(static mapping => mapping.DestinationCampaignId).Distinct().Count() > 1)
            {

                issues.Add(new BackupVerifyIssue(
                    DuplicateSourceCode,
                    $"Archived Campaign {group.Key:D} is mapped to more than one destination Campaign. "
                    + "Supply exactly one --map-campaign for it. "
                    + Unmodified));

            }

        }

        return issues;

    }

    /// <summary>
    /// Decides whether every mapped destination Campaign actually exists on this machine.
    /// </summary>
    /// <param name="request">The restore as the operator asked for it.</param>
    /// <param name="covenantImportArmActive">
    /// Whether this installation routes a selective import through the protected transfer store.
    /// </param>
    /// <param name="destinationCampaignIds">
    /// The Campaign identities the live Grimoire holds right now. Supplied rather than read here
    /// because Core opens no database.
    /// </param>
    /// <remarks>
    /// Silent when <see cref="EvaluateShape"/> already refused. "That Campaign does not exist here" is a
    /// second complaint about a mapping the first refusal has already disqualified, and reporting both
    /// would leave an operator fixing one and rediscovering the other.
    /// </remarks>
    public static IReadOnlyList<BackupVerifyIssue> EvaluateDestination(
        BackupRestoreRequest request,
        bool covenantImportArmActive,
        IReadOnlyCollection<Guid> destinationCampaignIds)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(destinationCampaignIds);

        if (EvaluateShape(request, covenantImportArmActive).Count > 0)
        {

            return [];

        }

        HashSet<Guid> present = [.. destinationCampaignIds];

        return
        [
            .. (request.CampaignMappings ?? [])
                .Select(static mapping => mapping.DestinationCampaignId)
                .Distinct()
                .Where(destination => !present.Contains(destination))
                .Select(static destination => new BackupVerifyIssue(
                    DestinationMissingCode,
                    $"Destination Campaign {destination:D} does not exist on this machine, so an "
                    + "imported Session could not be bound to it. Create or register the Campaign "
                    + "first, or map the archived Campaign to one this installation already has. "
                    + Unmodified)),
        ];

    }

}
