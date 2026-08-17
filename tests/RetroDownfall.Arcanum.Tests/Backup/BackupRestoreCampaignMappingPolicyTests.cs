using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// What an explicit source-to-destination Campaign mapping is allowed to say, decided before a
/// restore reads anything (§10.19.12).
/// </summary>
/// <remarks>
/// Pure and in Core, so the plan a rehearsal returns and the restore that executes it cannot disagree
/// about which mappings are well formed. Every refusal here happens while the destination is still
/// untouched, which is what lets an operator fix the command line rather than a half-imported Session.
/// </remarks>
public sealed class BackupRestoreCampaignMappingPolicyTests
{

    private static readonly Guid SourceCampaign =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid DestinationCampaign =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void A_request_without_mappings_needs_no_destination_read_and_refuses_nothing()
    {

        BackupRestoreRequest request = new("/tmp/archive.arcbackup");

        Assert.Empty(BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: true));

        Assert.False(BackupRestoreCampaignMappingPolicy.RequiresDestinationCampaigns(request, covenantImportArmActive: true));

    }

    [Fact]
    public void A_mapping_on_any_mode_but_a_selective_import_is_not_applicable()
    {

        BackupRestoreRequest request = new(
            "/tmp/archive.arcbackup",
            BackupRestoreConflictMode.ReplaceInstallation,
            CampaignMappings: [new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign)]);

        BackupVerifyIssue issue = Assert.Single(
            BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: true));

        Assert.Equal(BackupRestoreCampaignMappingPolicy.NotApplicableCode, issue.Code);

        // A refusal an operator cannot act on is not a refusal. The mode that does accept the mapping
        // is named in the message rather than left to be inferred.
        Assert.Contains("import-selected-sessions", issue.Message, StringComparison.Ordinal);

        // Nothing about a destination Campaign matters once the mapping cannot apply at all, so the
        // second read-only open of the live Grimoire is never paid for.
        Assert.False(BackupRestoreCampaignMappingPolicy.RequiresDestinationCampaigns(request, covenantImportArmActive: true));

    }

    [Fact]
    public void An_empty_identity_on_either_side_is_refused_before_anything_is_read()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(Guid.Empty, DestinationCampaign),
            new BackupSessionCampaignMapping(SourceCampaign, Guid.Empty));

        BackupVerifyIssue[] issues = [.. BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: true)];

        Assert.Equal(2, issues.Length);

        Assert.All(
            issues,
            static issue => Assert.Equal(
                BackupRestoreCampaignMappingPolicy.IncompleteCode,
                issue.Code));

    }

    [Fact]
    public void One_archived_Campaign_may_not_name_two_destinations()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign),
            new BackupSessionCampaignMapping(SourceCampaign, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")));

        BackupVerifyIssue issue = Assert.Single(
            BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: true));

        Assert.Equal(BackupRestoreCampaignMappingPolicy.DuplicateSourceCode, issue.Code);

        // The ambiguous archived Campaign is named, because the operator's next edit is to one of the
        // two --map-campaign values that mention it.
        Assert.Contains(SourceCampaign.ToString("D"), issue.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void The_same_mapping_stated_twice_is_one_unambiguous_instruction()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign),
            new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign));

        Assert.Empty(BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: true));

    }

    [Fact]
    public void A_destination_Campaign_this_machine_does_not_have_is_a_typed_refusal_naming_it()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign));

        BackupVerifyIssue issue = Assert.Single(
            BackupRestoreCampaignMappingPolicy.EvaluateDestination(request, covenantImportArmActive: true, []));

        Assert.Equal(BackupRestoreCampaignMappingPolicy.DestinationMissingCode, issue.Code);

        Assert.Contains(DestinationCampaign.ToString("D"), issue.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void A_destination_Campaign_that_exists_here_is_accepted()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign));

        Assert.True(BackupRestoreCampaignMappingPolicy.RequiresDestinationCampaigns(request, covenantImportArmActive: true));

        Assert.Empty(
            BackupRestoreCampaignMappingPolicy.EvaluateDestination(request, covenantImportArmActive: true, [DestinationCampaign]));

    }

    [Fact]
    public void A_shape_refusal_suppresses_the_destination_arm_rather_than_reporting_both()
    {

        BackupRestoreRequest request = new(
            "/tmp/archive.arcbackup",
            BackupRestoreConflictMode.NewProfileRoot,
            CampaignMappings: [new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign)]);

        // The mapping cannot apply here at all, so "that Campaign does not exist on this machine"
        // would be a second refusal about a mapping the first one already rejected.
        Assert.Empty(
            BackupRestoreCampaignMappingPolicy.EvaluateDestination(
                request,
                covenantImportArmActive: true,
                []));

    }

    [Fact]
    public void A_mapping_on_an_installation_with_no_Covenant_import_arm_is_refused_rather_than_ignored()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign));

        BackupVerifyIssue issue = Assert.Single(
            BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: false));

        Assert.Equal(BackupRestoreCampaignMappingPolicy.CovenantRequiredCode, issue.Code);

        // The pre-Covenant import writes every Session's CampaignId as NULL, so accepting the option
        // here would hand the operator exactly the silently unbound Session it exists to prevent.
        Assert.Contains("unbound", issue.Message, StringComparison.Ordinal);

        // And nothing is read to decide it: the mapping cannot be honoured however many Campaigns this
        // machine turns out to have.
        Assert.False(
            BackupRestoreCampaignMappingPolicy.RequiresDestinationCampaigns(
                request,
                covenantImportArmActive: false));

        Assert.Empty(
            BackupRestoreCampaignMappingPolicy.EvaluateDestination(
                request,
                covenantImportArmActive: false,
                []));

    }

    [Fact]
    public void Two_nil_sources_are_two_incomplete_mappings_rather_than_one_ambiguous_Campaign()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(Guid.Empty, DestinationCampaign),
            new BackupSessionCampaignMapping(Guid.Empty, Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")));

        BackupVerifyIssue[] issues = [
            .. BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: true)];

        // The nil identity is not a Campaign, so two of them are not one archived Campaign mapped
        // twice. Collapsing them into a duplicate-source complaint would name a Campaign that does
        // not exist and hide the two mappings that are actually malformed.
        Assert.Equal(2, issues.Length);

        Assert.All(
            issues,
            static issue => Assert.Equal(
                BackupRestoreCampaignMappingPolicy.IncompleteCode,
                issue.Code));

    }

    [Fact]
    public void Two_archived_Campaigns_sharing_one_absent_destination_are_reported_once()
    {

        BackupRestoreRequest request = Import(
            new BackupSessionCampaignMapping(SourceCampaign, DestinationCampaign),
            new BackupSessionCampaignMapping(
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                DestinationCampaign));

        // One missing Campaign, one refusal. Repeating it per mapping would make a two-Session import
        // read as two separate problems with the same single cause.
        BackupVerifyIssue issue = Assert.Single(
            BackupRestoreCampaignMappingPolicy.EvaluateDestination(
                request,
                covenantImportArmActive: true,
                []));

        Assert.Equal(BackupRestoreCampaignMappingPolicy.DestinationMissingCode, issue.Code);

    }

    [Fact]
    public void A_restore_that_names_no_mapping_is_untouched_by_the_arm_being_absent()
    {

        // The whole pre-Covenant import path sends no mappings and must stay byte-for-byte what it
        // was: the refusal above is about an option that was actually typed.
        BackupRestoreRequest request = new(
            "/tmp/archive.arcbackup",
            BackupRestoreConflictMode.ImportSelectedSessions,
            SessionIds: [Guid.Parse("11111111-1111-1111-1111-111111111111")]);

        Assert.Empty(
            BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive: false));

    }

    private static BackupRestoreRequest Import(params BackupSessionCampaignMapping[] mappings) =>
        new(
            "/tmp/archive.arcbackup",
            BackupRestoreConflictMode.ImportSelectedSessions,
            SessionIds: [Guid.Parse("11111111-1111-1111-1111-111111111111")],
            CampaignMappings: mappings);

}
