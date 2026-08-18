using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Issue #117 — the contracts of the one boundary every direct-deletion route dispatches through.
/// </summary>
/// <remarks>
/// The exhaustiveness of the thirteen-kind policy registry itself is proved by
/// <see cref="CovenantSensitiveArtifactPurgePolicyTests"/> and is deliberately not restated here: this
/// slice consumes that registry and adds no second one, so a copy of its assertions would be the second
/// opinion the design exists to prevent.
///
/// <para>What is proved here is the shape a caller sees. Three dispositions that mean three different
/// things, an authority slot that accepts exactly one publication of exactly one requirement, and a
/// target that cannot be constructed for a kind the registry does not cover.</para>
/// </remarks>
public sealed class CovenantSensitivePurgeBoundaryTests
{

    [Fact]
    public void Purge_dispositions_have_no_zero_member_so_a_default_never_reads_as_permission()
    {

        Assert.Equal(1, (int)CovenantSensitivePurgeDisposition.Unlabeled);

        Assert.Equal(2, (int)CovenantSensitivePurgeDisposition.Purged);

        Assert.Equal(3, (int)CovenantSensitivePurgeDisposition.Blocked);

        Assert.DoesNotContain(
            Enum.GetValues<CovenantSensitivePurgeDisposition>(),
            static disposition => (int)disposition == 0);

    }

    /// <summary>
    /// Only an unlabelled artifact tells the caller to run its own delete. Both other arms mean the
    /// caller deletes nothing — a purged row is already gone and a blocked one must stay.
    /// </summary>
    [Fact]
    public void Only_an_unlabeled_artifact_asks_the_caller_to_delete_it()
    {

        Guid unlabeled = new("11111111-1111-4111-8111-111111111111");

        Guid purged = new("22222222-2222-4222-8222-222222222222");

        Guid blocked = new("33333333-3333-4333-8333-333333333333");

        CovenantSensitivePurgeOutcome outcome = new(
            [
                new(unlabeled, SensitiveArtifactKind.Saga, CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None),
                new(purged, SensitiveArtifactKind.Saga, CovenantSensitivePurgeDisposition.Purged, CovenantErasureBlocker.None),
                new(blocked, SensitiveArtifactKind.Saga, CovenantSensitivePurgeDisposition.Blocked, CovenantErasureBlocker.ManualOwnershipMismatch),
            ],
            CovenantArtifactErasureProgress.Empty);

        Assert.True(outcome.RequiresOrdinaryDelete(unlabeled));

        Assert.False(outcome.RequiresOrdinaryDelete(purged));

        Assert.False(outcome.RequiresOrdinaryDelete(blocked));

        Assert.True(outcome.WasPurged(purged));

        Assert.False(outcome.WasPurged(blocked));

        Assert.True(outcome.IsBlocked);

        Assert.False(outcome.AllUnlabeled);

    }

    [Fact]
    public void A_batch_with_nothing_labeled_reports_all_unlabeled_and_is_not_blocked()
    {

        Guid artifactId = new("44444444-4444-4444-8444-444444444444");

        CovenantSensitivePurgeOutcome outcome = new(
            [
                new(artifactId, SensitiveArtifactKind.Lexicon, CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None),
            ],
            CovenantArtifactErasureProgress.Empty);

        Assert.True(outcome.AllUnlabeled);

        Assert.False(outcome.IsBlocked);

    }

    [Fact]
    public void A_purge_target_cannot_name_an_uncovered_kind_or_the_empty_identity()
    {

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            static () => new CovenantSensitivePurgeTarget((SensitiveArtifactKind)99, Guid.NewGuid()));

        _ = Assert.Throws<ArgumentException>(
            static () => new CovenantSensitivePurgeTarget(SensitiveArtifactKind.Saga, Guid.Empty));

    }

    /// <summary>
    /// The scope accepts exactly one publication of exactly one requirement.
    /// </summary>
    /// <remarks>
    /// Both refusals matter. A context issued for a protected read reaching the purger would let an
    /// inspection page authorize an erasure; a second publication would let anything downstream of the
    /// authenticated boundary swap the authority a route is acting under (§10.12).
    /// </remarks>
    [Fact]
    public void The_purge_authority_scope_is_write_once_and_accepts_only_its_own_requirement()
    {

        CovenantSensitivePurgeAuthorityScope scope = new();

        Assert.Null(scope.Current);

        OperatorAuthorityContext read = CovenantErasureAuthorityFixture.OperatorContext(
            new FakeCovenantAuthorityProvider(),
            CovenantAuthorityRequirement.ProtectedRead);

        Assert.True(scope.Publish(read).IsFailure);

        Assert.Null(scope.Current);

        OperatorAuthorityContext purge = CovenantErasureAuthorityFixture.OperatorContext(
            new FakeCovenantAuthorityProvider(),
            CovenantAuthorityRequirement.SensitivityRetentionPurge);

        Assert.True(scope.Publish(purge).IsSuccess);

        Assert.Same(purge, scope.Current);

        OperatorAuthorityContext second = CovenantErasureAuthorityFixture.OperatorContext(
            new FakeCovenantAuthorityProvider(),
            CovenantAuthorityRequirement.SensitivityRetentionPurge);

        Assert.True(scope.Publish(second).IsFailure);

        Assert.Same(purge, scope.Current);

    }

    /// <summary>
    /// The boundary is bounded, and both bounds are the same one the shared erasure page uses.
    /// </summary>
    /// <remarks>
    /// Not a coincidence worth leaving implicit: a purge page larger than the kernel's page would have to
    /// be split by the coordinator, which is the kind of second bookkeeping that drifts.
    /// </remarks>
    [Fact]
    public void The_purge_batch_bound_matches_the_shared_erasure_page_bound()
    {

        Assert.Equal(
            CovenantProtectedArtifactErasurePage.MaxItems,
            ICovenantSensitiveArtifactPurger.MaxTargets);

    }

}
