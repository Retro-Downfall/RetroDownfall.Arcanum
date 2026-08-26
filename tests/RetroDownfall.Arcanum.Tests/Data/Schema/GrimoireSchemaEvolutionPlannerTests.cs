using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The whole classification, decided without a database so every arm is directly reachable.
/// </summary>
/// <remarks>
/// The planner performs no I/O and therefore never answers the one question that needs a catalog
/// read - whether an installation recorded below head already has head's objects. That probe belongs
/// to the installer's evolve path and is proven against a real database in the installer suite.
/// </remarks>
public sealed class GrimoireSchemaEvolutionPlannerTests
{

    private const string OtherFingerprint =
        "2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void An_empty_database_installs_fresh_at_head()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(recorded: null, anyOwnedObjectPresent: false);

        Assert.Equal(GrimoireSchemaEvolutionAction.FreshInstall, decision.Action);

        Assert.Null(decision.Refusal);

    }

    [Fact]
    public void Objects_without_metadata_refuse_as_metadata_missing()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(recorded: null, anyOwnedObjectPresent: true);

        Assert.Equal(GrimoireSchemaTierHealth.MetadataMissing, decision.Refusal);

    }

    [Fact]
    public void A_version_above_head_refuses_as_incompatible()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(Recorded(3, OtherFingerprint));

        Assert.Equal(GrimoireSchemaTierHealth.IncompatibleNewerVersion, decision.Refusal);

    }

    [Fact]
    public void Head_with_the_head_fingerprint_converges()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaEvolutionFixture.TwoVersionChain();

        GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
            chain,
            new GrimoireSchemaRecordedTier(2, chain.HeadManifest.SourceDefinitionFingerprint),
            anyOwnedObjectPresent: true,
            journal: null);

        Assert.Equal(GrimoireSchemaEvolutionAction.Converge, decision.Action);

    }

    [Fact]
    public void Head_with_a_different_fingerprint_refuses_as_source_mismatch()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(Recorded(2, OtherFingerprint));

        Assert.Equal(GrimoireSchemaTierHealth.SourceDefinitionMismatch, decision.Refusal);

    }

    [Fact]
    public void An_older_version_with_the_pinned_fingerprint_begins_a_run()
    {

        GrimoireSchemaEvolutionDecision decision =
            Decide(Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint));

        Assert.Equal(GrimoireSchemaEvolutionAction.BeginRun, decision.Action);

        Assert.Equal(1, decision.ResumeFromVersion);

        Assert.Null(decision.PendingBackfillName);

    }

    /// <summary>
    /// The installed version 1 is not the version 1 this binary knows, so nothing may be run against
    /// it. This is the check that makes a pinned fingerprint worth carrying at all.
    /// </summary>
    [Fact]
    public void An_older_version_whose_recorded_fingerprint_is_not_the_pin_refuses_as_source_mismatch()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(Recorded(1, OtherFingerprint));

        Assert.Equal(GrimoireSchemaTierHealth.SourceDefinitionMismatch, decision.Refusal);

    }

    [Fact]
    public void A_resumable_journal_resumes_where_it_stopped()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            journal: Journal());

        Assert.Equal(GrimoireSchemaEvolutionAction.ResumeRun, decision.Action);

        Assert.Equal(1, decision.ResumeFromVersion);

        Assert.Null(decision.PendingBackfillName);

    }

    [Fact]
    public void A_resumable_journal_reports_the_sweep_still_draining()
    {

        GrimoireSchemaVersionChain chain = GrimoireSchemaEvolutionFixture.ChainWithSteps(
            headVersion: 2,
            GrimoireSchemaEvolutionFixture.Step(1, 2, backfill: new TestBackfill("fill-target")));

        GrimoireSchemaEvolutionDecision decision = GrimoireSchemaEvolutionPlanner.Decide(
            chain,
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            anyOwnedObjectPresent: true,
            Journal(chain) with { BackfillName = "fill-target", BackfillCursor = "7" });

        Assert.Equal(GrimoireSchemaEvolutionAction.ResumeRun, decision.Action);

        Assert.Equal("fill-target", decision.PendingBackfillName);

    }

    [Fact]
    public void A_journal_with_no_metadata_row_is_unresumable()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(recorded: null, journal: Journal());

        Assert.Equal(GrimoireSchemaTierHealth.TransitionUnresumable, decision.Refusal);

    }

    [Fact]
    public void A_journal_targeting_a_version_above_head_is_unresumable()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            journal: Journal() with { TargetVersion = 3 });

        Assert.Equal(GrimoireSchemaTierHealth.TransitionUnresumable, decision.Refusal);

    }

    /// <summary>
    /// A binary swapped mid-run cannot finish a run some other head defined.
    /// </summary>
    [Fact]
    public void A_journal_recording_a_different_head_is_unresumable()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            journal: Journal() with { TargetSourceDefinitionFingerprint = OtherFingerprint });

        Assert.Equal(GrimoireSchemaTierHealth.TransitionUnresumable, decision.Refusal);

    }

    [Fact]
    public void A_journal_disagreeing_with_the_metadata_version_is_unresumable()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            journal: Journal() with { FromVersion = 2, CompletedThroughVersion = 2 });

        Assert.Equal(GrimoireSchemaTierHealth.TransitionUnresumable, decision.Refusal);

    }

    [Fact]
    public void A_journal_stopped_at_a_version_the_chain_cannot_leave_is_unresumable()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            journal: Journal() with { CompletedThroughVersion = 9 });

        Assert.Equal(GrimoireSchemaTierHealth.TransitionUnresumable, decision.Refusal);

    }

    [Fact]
    public void A_journal_naming_a_sweep_the_step_does_not_declare_is_unresumable()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            journal: Journal() with { BackfillName = "a-sweep-nobody-declares" });

        Assert.Equal(GrimoireSchemaTierHealth.TransitionUnresumable, decision.Refusal);

    }

    [Fact]
    public void A_journal_belonging_to_another_family_is_unresumable()
    {

        GrimoireSchemaEvolutionDecision decision = Decide(
            Recorded(1, GrimoireSchemaEvolutionFixture.VersionOneFingerprint),
            journal: Journal() with { Family = GrimoireSchemaFamily.Covenant });

        Assert.Equal(GrimoireSchemaTierHealth.TransitionUnresumable, decision.Refusal);

    }

    private static GrimoireSchemaRecordedTier Recorded(int version, string fingerprint) =>
        new(version, fingerprint);

    private static GrimoireSchemaTransitionJournalRow Journal(GrimoireSchemaVersionChain? chain = null)
    {

        GrimoireSchemaVersionChain resolved = chain ?? GrimoireSchemaEvolutionFixture.TwoVersionChain();

        return new GrimoireSchemaTransitionJournalRow(
            GrimoireSchemaFamily.Core,
            GrimoireSchemaTransactionTier.Core,
            FromVersion: 1,
            TargetVersion: 2,
            CompletedThroughVersion: 1,
            resolved.HeadManifest.SourceDefinitionFingerprint,
            BackfillName: null,
            BackfillCursor: null,
            BackfillRowsProcessed: 0,
            Revision: 0);

    }

    private static GrimoireSchemaEvolutionDecision Decide(
        GrimoireSchemaRecordedTier? recorded,
        bool anyOwnedObjectPresent = true,
        GrimoireSchemaTransitionJournalRow? journal = null) =>
        GrimoireSchemaEvolutionPlanner.Decide(
            GrimoireSchemaEvolutionFixture.TwoVersionChain(),
            recorded,
            anyOwnedObjectPresent,
            journal);

}
