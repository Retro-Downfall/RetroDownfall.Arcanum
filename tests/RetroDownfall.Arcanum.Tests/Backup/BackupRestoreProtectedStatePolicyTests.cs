using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The one place a restore decides what it may do with the protected state an archive carries
/// (§10.19.10).
/// </summary>
/// <remarks>
/// A pure suite over a pure decision, because every arm of it is a refusal an operator has to be able
/// to predict from the mode they typed. The two evaluation points are separate on purpose: the request
/// arm answers before the archive has been read at all, and the archive arm answers after extraction
/// and before the staged generation exists.
/// </remarks>
public sealed class BackupRestoreProtectedStatePolicyTests
{

    private static readonly BackupRestoreProtectedStateInventory Protected =
        new(CanonicalRows: 4, ProtectedArtifacts: 2, SourceAuthorityTainted: false);

    private static readonly BackupRestoreProtectedStateInventory TaintedAndProtected =
        new(CanonicalRows: 4, ProtectedArtifacts: 2, SourceAuthorityTainted: true);

    private static readonly BackupRestoreProtectedStateInventory TaintedOnly =
        new(CanonicalRows: 0, ProtectedArtifacts: 0, SourceAuthorityTainted: true);

    [Fact]
    public void The_default_request_proceeds_whether_or_not_the_Covenant_arm_is_active()
    {

        Assert.Equal(
            BackupRestoreProtectedStateOutcome.Proceed,
            BackupRestoreProtectedStatePolicy
                .EvaluateRequest(Request(BackupProtectedStateMode.Reject), covenantArmActive: true)
                .Outcome);

        // The pre-Covenant path is unchanged by this slice, so the default must not acquire a new way
        // to refuse on an installation that never enabled the gate.
        Assert.Equal(
            BackupRestoreProtectedStateOutcome.Proceed,
            BackupRestoreProtectedStatePolicy
                .EvaluateRequest(Request(BackupProtectedStateMode.Reject), covenantArmActive: false)
                .Outcome);

    }

    [Theory]
    [InlineData(BackupProtectedStateMode.RestoreProtectedState)]
    [InlineData(BackupProtectedStateMode.PurgeProtectedState)]
    public void A_destructive_mode_requires_its_own_confirmation(BackupProtectedStateMode mode)
    {

        BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy.EvaluateRequest(
            Request(mode, protectedStateConfirmed: false),
            covenantArmActive: true);

        Assert.Equal(BackupRestoreProtectedStateOutcome.Refuse, decision.Outcome);

        Assert.Equal(
            BackupRestoreProtectedStatePolicy.ConfirmationRequiredCode,
            decision.Blocker?.Code);

        Assert.Equal(
            BackupRestoreProtectedStateOutcome.Proceed,
            BackupRestoreProtectedStatePolicy
                .EvaluateRequest(Request(mode, protectedStateConfirmed: true), covenantArmActive: true)
                .Outcome);

    }

    [Fact]
    public void A_rehearsal_never_refuses_for_want_of_the_confirmation_it_exists_to_compose()
    {

        BackupRestoreRequest request = Request(
            BackupProtectedStateMode.PurgeProtectedState,
            protectedStateConfirmed: false);

        // The shape arm answers the questions a plan may report; confirmation is not one of them,
        // because the surface reads the plan in order to ask for it.
        Assert.Equal(
            BackupRestoreProtectedStateOutcome.Proceed,
            BackupRestoreProtectedStatePolicy.EvaluateRequestShape(request, covenantArmActive: true)
                .Outcome);

        // Applicability still refuses in both arms, so the two can never disagree about it.
        BackupRestoreRequest inapplicable = request with
        {

            ConflictMode = BackupRestoreConflictMode.NewProfileRoot,

        };

        Assert.Equal(
            BackupRestoreProtectedStatePolicy.ModeNotApplicableCode,
            BackupRestoreProtectedStatePolicy.EvaluateRequestShape(inapplicable, covenantArmActive: true)
                .Blocker?.Code);

        Assert.Equal(
            BackupRestoreProtectedStatePolicy.ModeNotApplicableCode,
            BackupRestoreProtectedStatePolicy.EvaluateRequest(inapplicable, covenantArmActive: true)
                .Blocker?.Code);

    }

    [Fact]
    public void The_replacement_confirmation_is_not_the_protected_state_confirmation()
    {

        BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy.EvaluateRequest(
            Request(BackupProtectedStateMode.PurgeProtectedState) with { Confirmed = true },
            covenantArmActive: true);

        Assert.Equal(BackupRestoreProtectedStateOutcome.Refuse, decision.Outcome);

        Assert.Equal(
            BackupRestoreProtectedStatePolicy.ConfirmationRequiredCode,
            decision.Blocker?.Code);

    }

    [Theory]
    [InlineData(BackupRestoreConflictMode.NewProfileRoot)]
    [InlineData(BackupRestoreConflictMode.ImportSelectedSessions)]
    public void A_protected_state_mode_applies_only_to_a_replacement(BackupRestoreConflictMode conflict)
    {

        BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy.EvaluateRequest(
            Request(BackupProtectedStateMode.PurgeProtectedState, protectedStateConfirmed: true)
                with { ConflictMode = conflict },
            covenantArmActive: true);

        Assert.Equal(BackupRestoreProtectedStateOutcome.Refuse, decision.Outcome);

        Assert.Equal(
            BackupRestoreProtectedStatePolicy.ModeNotApplicableCode,
            decision.Blocker?.Code);

    }

    [Theory]
    [InlineData(BackupProtectedStateMode.RestoreProtectedState)]
    [InlineData(BackupProtectedStateMode.PurgeProtectedState)]
    public void A_protected_state_mode_refuses_rather_than_pretending_the_gate_is_on(
        BackupProtectedStateMode mode)
    {

        BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy.EvaluateRequest(
            Request(mode, protectedStateConfirmed: true),
            covenantArmActive: false);

        Assert.Equal(BackupRestoreProtectedStateOutcome.Refuse, decision.Outcome);

        Assert.Equal(
            BackupRestoreProtectedStatePolicy.CovenantRequiredCode,
            decision.Blocker?.Code);

    }

    [Fact]
    public void The_default_refuses_an_archive_that_carries_any_protected_state()
    {

        foreach (BackupRestoreProtectedStateInventory inventory in new[]
                 {
                     Protected,
                     TaintedAndProtected,
                     new BackupRestoreProtectedStateInventory(1, 0, false),
                     new BackupRestoreProtectedStateInventory(0, 1, false),
                     new BackupRestoreProtectedStateInventory(0, 0, false, AcceleratorRows: 1),
                 })
        {

            BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.Reject, inventory);

            Assert.Equal(BackupRestoreProtectedStateOutcome.Refuse, decision.Outcome);

            Assert.Equal(
                BackupRestoreProtectedStatePolicy.ProtectedStatePresentCode,
                decision.Blocker?.Code);

        }

    }

    [Fact]
    public void The_default_proceeds_over_an_archive_that_carries_none()
    {

        Assert.Equal(
            BackupRestoreProtectedStateOutcome.Proceed,
            BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.Reject, BackupRestoreProtectedStateInventory.None)
                .Outcome);

        // A tainted source with nothing protected in it has nothing to promote. The taint itself still
        // joins into the destination monotonically, which is not this policy's decision to make.
        Assert.Equal(
            BackupRestoreProtectedStateOutcome.Proceed,
            BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.Reject, TaintedOnly)
                .Outcome);

    }

    [Fact]
    public void Preserving_protected_state_requires_a_clean_source()
    {

        Assert.Equal(
            BackupRestoreProtectedStateOutcome.Proceed,
            BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.RestoreProtectedState, Protected)
                .Outcome);

        BackupRestoreProtectedStateDecision refused = BackupRestoreProtectedStatePolicy
            .EvaluateArchive(BackupProtectedStateMode.RestoreProtectedState, TaintedAndProtected);

        Assert.Equal(BackupRestoreProtectedStateOutcome.Refuse, refused.Outcome);

        Assert.Equal(BackupRestoreProtectedStatePolicy.SourceTaintedCode, refused.Blocker?.Code);

    }

    [Fact]
    public void A_purge_is_the_only_continuation_a_source_tainted_archive_has()
    {

        // Both non-purge modes fail closed, and the purge is what the refusal names.
        foreach (BackupProtectedStateMode refusing in new[]
                 {
                     BackupProtectedStateMode.Reject,
                     BackupProtectedStateMode.RestoreProtectedState,
                 })
        {

            BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy
                .EvaluateArchive(refusing, TaintedAndProtected);

            Assert.Equal(BackupRestoreProtectedStateOutcome.Refuse, decision.Outcome);

            Assert.Contains(
                "purge-protected-state",
                decision.Blocker?.Message ?? string.Empty,
                StringComparison.Ordinal);

        }

        Assert.Equal(
            BackupRestoreProtectedStateOutcome.PurgeStaging,
            BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.PurgeProtectedState, TaintedAndProtected)
                .Outcome);

    }

    [Fact]
    public void A_purge_stages_its_removal_whether_or_not_the_archive_carries_anything()
    {

        foreach (BackupRestoreProtectedStateInventory inventory in new[]
                 {
                     BackupRestoreProtectedStateInventory.None,
                     Protected,
                     TaintedAndProtected,
                     TaintedOnly,
                 })
        {

            BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.PurgeProtectedState, inventory);

            Assert.Equal(BackupRestoreProtectedStateOutcome.PurgeStaging, decision.Outcome);

            Assert.Null(decision.Blocker);

        }

    }

    [Fact]
    public void Every_refusal_says_the_current_installation_was_not_modified()
    {

        BackupVerifyIssue[] refusals =
        [
            BackupRestoreProtectedStatePolicy
                .EvaluateRequest(
                    Request(BackupProtectedStateMode.PurgeProtectedState),
                    covenantArmActive: true)
                .Blocker!,
            BackupRestoreProtectedStatePolicy
                .EvaluateRequest(
                    Request(BackupProtectedStateMode.PurgeProtectedState, protectedStateConfirmed: true)
                        with { ConflictMode = BackupRestoreConflictMode.NewProfileRoot },
                    covenantArmActive: true)
                .Blocker!,
            BackupRestoreProtectedStatePolicy
                .EvaluateRequest(
                    Request(BackupProtectedStateMode.PurgeProtectedState, protectedStateConfirmed: true),
                    covenantArmActive: false)
                .Blocker!,
            BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.Reject, Protected)
                .Blocker!,
            BackupRestoreProtectedStatePolicy
                .EvaluateArchive(BackupProtectedStateMode.RestoreProtectedState, TaintedAndProtected)
                .Blocker!,
        ];

        foreach (BackupVerifyIssue refusal in refusals)
        {

            Assert.Contains(
                "was not modified",
                refusal.Message,
                StringComparison.Ordinal);

            // Content-free: a refusal travels into logs and operator surfaces, so it names no path,
            // Session, Campaign, or artifact.
            Assert.Null(refusal.Path);

        }

    }

    [Fact]
    public void An_inventory_counts_canonical_rows_projections_and_labels_independently()
    {

        Assert.False(BackupRestoreProtectedStateInventory.None.CarriesProtectedState);

        Assert.True(new BackupRestoreProtectedStateInventory(1, 0, false).CarriesProtectedState);

        Assert.True(new BackupRestoreProtectedStateInventory(0, 1, false).CarriesProtectedState);

        Assert.True(
            new BackupRestoreProtectedStateInventory(0, 0, false, AcceleratorRows: 1)
                .CarriesProtectedState);

        Assert.False(TaintedOnly.CarriesProtectedState);

    }

    private static BackupRestoreRequest Request(
        BackupProtectedStateMode mode,
        bool protectedStateConfirmed = false) =>
        new(
            "/tmp/archive.arcbackup",
            ProtectedStateMode: mode,
            ProtectedStateConfirmed: protectedStateConfirmed);

}
