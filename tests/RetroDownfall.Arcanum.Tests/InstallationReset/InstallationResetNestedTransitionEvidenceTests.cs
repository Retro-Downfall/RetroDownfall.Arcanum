using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The two durable records resolved as one pair, which is the only way either of them means anything.
/// </summary>
/// <remarks>
/// Each record on its own has a state that reads as benign and is not. An installation reset holding a
/// claim with no receipt looks like a reset in progress, but it is a reset whose nested database
/// transition started and whose journal is gone. A journal carrying a parent binding looks like an
/// ordinary erasure to resume, but resuming it as standalone work would let a nested transition finish
/// without the workflow that launched it ever hearing so. Both are refusals, and neither is visible
/// from one record.
/// </remarks>
public sealed class InstallationResetNestedTransitionEvidenceTests
{

    private static readonly Guid Outer = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static readonly Guid Nested = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public void Neither_record_active_is_an_ordinary_launch()
    {

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.NeitherActive,
            InstallationResetNestedTransitionEvidence.Resolve(NoReset(), NoJournal()));

    }

    [Fact]
    public void A_reset_alone_reads_from_how_far_its_nested_arm_got()
    {

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.NestedNotStarted,
            InstallationResetNestedTransitionEvidence.Resolve(Reset(receipt: null), NoJournal()));

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.NestedRetired,
            InstallationResetNestedTransitionEvidence.Resolve(Reset(Completed()), NoJournal()));

        // A claim with no journal and no receipt is the one arm that cannot be read forward. The
        // transition started; treating it as never started would relaunch a destructive plan.
        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
            InstallationResetNestedTransitionEvidence.Resolve(Reset(Claimed()), NoJournal()));

    }

    [Fact]
    public void A_journal_alone_may_be_standalone_and_may_never_be_downgraded()
    {

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition,
            InstallationResetNestedTransitionEvidence.Resolve(
                NoReset(),
                Journal(parentBinding: null)));

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
            InstallationResetNestedTransitionEvidence.Resolve(
                NoReset(),
                Journal(Binding())));

    }

    [Fact]
    public void Both_active_requires_the_exact_binding_the_outer_record_reproduces()
    {

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.NestedBound,
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Claimed()),
                Journal(Binding())));

        // Standalone work beside an active reset, a nested direct reset, and a binding the record
        // cannot reproduce are each two records describing different work under one identity.
        InstallationResetNestedTransitionEvidenceOutcome[] refused =
        [
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Claimed()),
                Journal(parentBinding: null)),
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Claimed()),
                Journal(Binding(), GrimoireOfflineTransitionKind.CovenantReset)),
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Claimed()),
                Journal(Digest(0x99))),
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(receipt: null),
                Journal(Binding())),
        ];

        Assert.All(
            refused,
            static outcome => Assert.Equal(
                InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
                outcome));

    }

    [Fact]
    public void A_stored_receipt_admits_only_the_retirement_suffix()
    {

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.NestedReceiptStoredRetirementSuffix,
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Completed()),
                Journal(
                    Binding(),
                    state: GrimoireOfflineTransitionState.RetirementPending)));

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.NestedReceiptStoredRetirementSuffix,
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Completed()),
                Journal(
                    Binding(),
                    state: GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                    step: GrimoireOfflineTransitionReconciliationStep.ParentReceiptSatisfied)));

        // The receipt says the database is already terminal. A journal sitting anywhere before its own
        // parent step contradicts that, and a suffix published to catch up would record steps this
        // transition never performed.
        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Completed()),
                Journal(Binding(), state: GrimoireOfflineTransitionState.Applying)));

        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Completed()),
                Journal(
                    Binding(),
                    state: GrimoireOfflineTransitionState.DatabaseReconciliationPending,
                    step: GrimoireOfflineTransitionReconciliationStep.DatabaseTerminalWinner)));

    }

    [Fact]
    public void Two_records_naming_two_different_terminal_winners_fail_closed()
    {

        // The one cross-record check neither record can make alone. Both name the row this transition
        // terminalized; if they disagree, one of them is describing a different run.
        Assert.Equal(
            InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired,
            InstallationResetNestedTransitionEvidence.Resolve(
                Reset(Completed() with { TerminalWinnerDigest = Digest(0x55) }),
                Journal(
                    Binding(),
                    state: GrimoireOfflineTransitionState.RetirementPending)));

    }

    [Fact]
    public void Only_the_three_active_journal_outcomes_stop_a_host_from_bootstrapping()
    {

        // The refusals are the point: a host that bootstrapped over an active journal would open the
        // catalog the transition closed admission to keep shut. What it may not do is refuse on the
        // three states where no transition is in flight, or the product could never start again after
        // an ordinary reset.
        Assert.True(
            InstallationResetHostStartupAdmission.LeavesTransitionUnfinished(
                InstallationResetNestedTransitionEvidenceOutcome.StandaloneTransition));

        Assert.True(
            InstallationResetHostStartupAdmission.LeavesTransitionUnfinished(
                InstallationResetNestedTransitionEvidenceOutcome.NestedBound));

        Assert.True(
            InstallationResetHostStartupAdmission.LeavesTransitionUnfinished(
                InstallationResetNestedTransitionEvidenceOutcome.NestedReceiptStoredRetirementSuffix));

        InstallationResetNestedTransitionEvidenceOutcome?[] proceeding =
        [
            null,
            InstallationResetNestedTransitionEvidenceOutcome.NeitherActive,
            InstallationResetNestedTransitionEvidenceOutcome.NestedNotStarted,
            InstallationResetNestedTransitionEvidenceOutcome.NestedRetired,
        ];

        Assert.All(
            proceeding,
            static outcome => Assert.False(
                InstallationResetHostStartupAdmission.LeavesTransitionUnfinished(outcome)));

        // RecoveryRequired never reaches this predicate: startup recovery has already turned it into
        // a content-free refusal, and a second reading of it here would be a second authority on the
        // same fact.
        Assert.False(
            InstallationResetHostStartupAdmission.LeavesTransitionUnfinished(
                InstallationResetNestedTransitionEvidenceOutcome.RecoveryRequired));

    }

    private static CovenantDigest Effect => Digest(0x11);

    private static CovenantDigest Winner => Digest(0x31);

    private static CovenantDigest Binding() =>
        GrimoireOfflineTransitionParentReceipt.BindingDigest(Outer, Nested, Effect).Value;

    private static InstallationResetNestedTransitionReceiptV1 Claimed() =>
        new(
            Version: 1,
            Nested,
            InstallationResetNestedTransitionPhase.Claimed,
            NestedEffectDigest: null,
            TerminalWinnerDigest: null);

    private static InstallationResetNestedTransitionReceiptV1 Completed() =>
        Claimed() with
        {
            Phase = InstallationResetNestedTransitionPhase.Completed,
            NestedEffectDigest = Effect,
            TerminalWinnerDigest = Winner,
        };

    private static InstallationResetNestedTransitionEvidence.OuterRecord? NoReset() => null;

    private static InstallationResetNestedTransitionEvidence.OuterRecord Reset(
        InstallationResetNestedTransitionReceiptV1? receipt) =>
        new(Outer, receipt);

    private static InstallationResetNestedTransitionEvidence.InnerJournal? NoJournal() => null;

    private static InstallationResetNestedTransitionEvidence.InnerJournal Journal(
        CovenantDigest? parentBinding,
        GrimoireOfflineTransitionKind kind =
            GrimoireOfflineTransitionKind.HealthyCatalogFactoryErasure,
        GrimoireOfflineTransitionState state = GrimoireOfflineTransitionState.Applying,
        GrimoireOfflineTransitionReconciliationStep? step = null) =>
        new(
            Nested,
            kind,
            Effect,
            parentBinding,
            state,
            step,
            state is GrimoireOfflineTransitionState.RetirementPending ? Winner : null);

    private static CovenantDigest Digest(byte first) =>
        new([.. Enumerable.Range(first, 32).Select(static value => (byte)value)]);

}
