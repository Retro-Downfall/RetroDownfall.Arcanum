using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The process-wide Covenant availability publisher: what a hot-path gate reads, and what each
/// publisher is allowed to move when it writes.
/// </summary>
/// <remarks>
/// Two properties carry the design. First, a publication swaps one complete immutable snapshot, so
/// no reader can observe a fresh dataset generation beside a stale applied sequence and conclude the
/// accelerator is current when it has just been reset. Second, each publisher copies only the fields
/// it owns; letting a mutation commit write the accelerator's applied tuple would let an unrelated
/// writer silently mark search current.
///
/// <para>The enum codes are pinned literal by literal rather than compared as a set, because their
/// numbers are persisted and serialized. Renumbering one is a wire-format change that has to be a
/// deliberate edit to this test, not a side effect of inserting a member.</para>
/// </remarks>
public sealed class CovenantAvailabilityTests
{

    /// <summary>
    /// The installed-catalog fingerprint form the installer produces: the lowercase <c>sha256-</c>
    /// prefix plus 64 hex characters. Fixed rather than computed, so an assertion failure is a real
    /// difference and not a hash that moved with an unrelated schema edit.
    /// </summary>
    private const string CanonicalFingerprint =
        "sha256-1111111111111111111111111111111111111111111111111111111111111111";

    private const string AcceleratorFingerprint =
        "sha256-2222222222222222222222222222222222222222222222222222222222222222";

    private const string SourceFingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private const int SchemaVersion = 1;

    /// <summary>
    /// A fixed dataset generation. Every turn snapshot binds this value, so the test uses one
    /// literal for both the canonical and the applied side to prove the two round-trip separately
    /// rather than by sharing a variable the publisher could have crossed.
    /// </summary>
    private static readonly Guid DatasetGeneration = new("2F1A9C7E-4B63-4D18-9E52-0A7C6D3B8F41");

    private static readonly Guid NextDatasetGeneration = new("7D44D16A-1D26-4966-83F4-E53D55CB90C7");

    /// <summary>
    /// Every enum code that reaches storage or an operator surface, pinned independently of
    /// declaration order.
    /// </summary>
    /// <remarks>
    /// No capability or synchronization member is zero on purpose: a zero would be indistinguishable
    /// from a default-initialized field, and "accidentally healthy" is the one wrong answer these
    /// types must never give. The length assertions catch a member added without a decision about
    /// what its number means.
    /// </remarks>
    [Fact]
    public void Availability_enum_codes_are_immutable()
    {

        Assert.Equal(1, (int)CovenantCapabilityState.Unavailable);

        Assert.Equal(2, (int)CovenantCapabilityState.Degraded);

        Assert.Equal(3, (int)CovenantCapabilityState.Healthy);

        Assert.Equal(3, Enum.GetValues<CovenantCapabilityState>().Length);

        Assert.Equal(1, (int)CovenantFtsSynchronizationState.Unavailable);

        Assert.Equal(2, (int)CovenantFtsSynchronizationState.Dirty);

        Assert.Equal(3, (int)CovenantFtsSynchronizationState.Synchronized);

        Assert.Equal(3, Enum.GetValues<CovenantFtsSynchronizationState>().Length);

        Assert.Equal(1, (int)CovenantHealthTransition.Bootstrap);

        Assert.Equal(2, (int)CovenantHealthTransition.SchemaRepair);

        Assert.Equal(3, (int)CovenantHealthTransition.CanonicalMutation);

        Assert.Equal(4, (int)CovenantHealthTransition.OwnerCleanup);

        Assert.Equal(5, (int)CovenantHealthTransition.AcceleratorSynchronization);

        Assert.Equal(6, (int)CovenantHealthTransition.AcceleratorRebuild);

        Assert.Equal(7, (int)CovenantHealthTransition.Reset);

        Assert.Equal(8, (int)CovenantHealthTransition.Restore);

        Assert.Equal(9, (int)CovenantHealthTransition.FamilyReinitialize);

        Assert.Equal(10, (int)CovenantHealthTransition.FeatureConfiguration);

        Assert.Equal(10, Enum.GetValues<CovenantHealthTransition>().Length);

    }

    /// <summary>
    /// Every publication advances the generation, including one that publishes the same schema
    /// outcome twice.
    /// </summary>
    /// <remarks>
    /// The generation is what lets a turn capture availability early and prove at dispatch time that
    /// nothing moved underneath it. A publisher that skipped the advance because "nothing changed"
    /// would make a stale capture look current, so the counter tracks publications rather than
    /// differences.
    /// </remarks>
    [Fact]
    public void Availability_generation_advances_on_each_published_install_result()
    {

        CovenantAvailability availability = new();

        long initial = availability.Current.Generation;

        CovenantAvailabilitySnapshot first = availability.PublishSchema(
            HealthyInstallResult(),
            CovenantHealthTransition.Bootstrap);

        Assert.Equal(initial + 1, first.Generation);

        CovenantAvailabilitySnapshot second = availability.PublishSchema(
            HealthyInstallResult(),
            CovenantHealthTransition.SchemaRepair);

        Assert.Equal(initial + 2, second.Generation);

        CovenantAvailabilitySnapshot third = availability.PublishSchema(
            HealthyInstallResult(),
            CovenantHealthTransition.Restore);

        Assert.Equal(initial + 3, third.Generation);

        Assert.Equal(CovenantHealthTransition.Restore, availability.Current.LastHealthTransition);

        Assert.Equal(third.Generation, availability.Current.Generation);

    }

    [Fact]
    public void A_committed_reset_builds_the_complete_tuple_in_one_generation()
    {

        CovenantAvailability availability = new();

        _ = availability.PublishSchema(HealthyInstallResult(), CovenantHealthTransition.Bootstrap);

        CovenantAvailabilitySnapshot expected = availability.PublishCanonicalState(
            DatasetGeneration,
            canonicalSequence: 42,
            coreCampaignDeletionSequence: 7,
            rebuildRequired: false,
            CovenantHealthTransition.CanonicalMutation);

        CovenantCommittedCapabilityTransition transition = new(
            expected.Generation,
            expected.Generation + 1,
            FeatureEnabled: true,
            CovenantCapabilityState.Healthy,
            SchemaVersion,
            CanonicalFingerprint,
            CovenantCapabilityState.Healthy,
            SchemaVersion,
            AcceleratorFingerprint,
            NextDatasetGeneration,
            CanonicalSequence: 0,
            CoreCampaignDeletionSequence: 8,
            CanonicalAppliedCampaignDeletionSequence: 8,
            CanonicalAppliedSessionDeletionSequence: 5,
            AppliedDatasetGeneration: null,
            AppliedSequence: null,
            AppliedCampaignDeletionSequence: null,
            AcceleratorEpoch: 13,
            CovenantFtsSynchronizationState.Dirty,
            RebuildRequired: true,
            CleanupAppliedCampaignSequence: 8,
            CleanupAppliedSessionSequence: 5,
            CleanupFullSweepRequired: false,
            CanonicalDiagnosticCode: null,
            AcceleratorDiagnosticCode: null);

        Result<CovenantAvailabilitySnapshot> built = availability.BuildCommittedTransition(
            expected,
            transition,
            CovenantHealthTransition.Reset);

        Assert.True(built.IsSuccess);

        Assert.Equal(expected.Generation + 1, built.Value.Generation);

        Assert.True(built.Value.FeatureEnabled);

        Assert.Equal(NextDatasetGeneration, built.Value.DatasetGeneration);

        Assert.Equal(0, built.Value.CanonicalSequence);

        Assert.Equal(8, built.Value.CoreCampaignDeletionSequence);

        Assert.Null(built.Value.AppliedDatasetGeneration);

        Assert.Null(built.Value.AppliedSequence);

        Assert.Null(built.Value.AppliedCampaignDeletionSequence);

        Assert.Equal(13UL, built.Value.AcceleratorEpoch);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, built.Value.FtsSynchronization);

        Assert.True(built.Value.RebuildRequired);

        Assert.Equal(CovenantHealthTransition.Reset, built.Value.LastHealthTransition);

        Assert.Same(expected, availability.Current);

    }

    /// <summary>
    /// Every field a hot-path gate compares survives publication with the value its owner wrote.
    /// </summary>
    /// <remarks>
    /// The canonical and applied tuples are published by different callers and asserted separately
    /// here, because the failure this guards against is one publisher's value landing in the other's
    /// field. A gate that read a crossed pair would decide the accelerator was current from the
    /// canonical side's own numbers.
    /// </remarks>
    [Fact]
    public void Availability_snapshot_carries_every_hot_gate_cursor_and_status_field()
    {

        CovenantAvailability availability = new();

        _ = availability.PublishSchema(HealthyInstallResult(), CovenantHealthTransition.Bootstrap);

        _ = availability.PublishCanonicalState(
            DatasetGeneration,
            canonicalSequence: 42,
            coreCampaignDeletionSequence: 7,
            rebuildRequired: true,
            CovenantHealthTransition.CanonicalMutation);

        CovenantAvailabilitySnapshot snapshot = availability.PublishAcceleratorState(
            appliedDatasetGeneration: DatasetGeneration,
            appliedSequence: 41,
            appliedCampaignDeletionSequence: 6,
            acceleratorEpoch: 3,
            CovenantFtsSynchronizationState.Synchronized,
            rebuildRequired: false,
            CovenantHealthTransition.AcceleratorSynchronization);

        Assert.Equal(4L, snapshot.Generation);

        Assert.False(snapshot.FeatureEnabled);

        Assert.Equal(CovenantCapabilityState.Healthy, snapshot.Canonical);

        Assert.Equal(SchemaVersion, snapshot.CanonicalSchemaVersion);

        Assert.Equal(CanonicalFingerprint, snapshot.CanonicalInstalledFingerprint);

        Assert.Equal(CovenantCapabilityState.Healthy, snapshot.Accelerator);

        Assert.Equal(SchemaVersion, snapshot.AcceleratorSchemaVersion);

        Assert.Equal(AcceleratorFingerprint, snapshot.AcceleratorInstalledFingerprint);

        Assert.Equal(DatasetGeneration, snapshot.DatasetGeneration);

        Assert.Equal(42L, snapshot.CanonicalSequence);

        Assert.Equal(7L, snapshot.CoreCampaignDeletionSequence);

        Assert.Equal(DatasetGeneration, snapshot.AppliedDatasetGeneration);

        Assert.Equal((long?)41L, snapshot.AppliedSequence);

        Assert.Equal((long?)6L, snapshot.AppliedCampaignDeletionSequence);

        Assert.Equal(3UL, snapshot.AcceleratorEpoch);

        Assert.Equal(CovenantFtsSynchronizationState.Synchronized, snapshot.FtsSynchronization);

        Assert.False(snapshot.RebuildRequired);

        Assert.Equal(
            CovenantHealthTransition.AcceleratorSynchronization,
            snapshot.LastHealthTransition);

        Assert.Null(snapshot.CanonicalDiagnosticCode);

        Assert.Null(snapshot.AcceleratorDiagnosticCode);

        Assert.Same(snapshot, availability.Current);

    }

    /// <summary>
    /// Flipping the feature switch changes the flag, the transition, and the generation, and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// The whole record is compared against the previous one rebuilt with only those three fields
    /// changed, which is the only assertion that proves a field was not disturbed without naming
    /// every field. A live configuration change must not be able to move a cursor a turn already
    /// captured.
    /// </remarks>
    [Fact]
    public void Feature_publication_advances_generation_without_schema_reinstall()
    {

        CovenantAvailability availability = new();

        _ = availability.PublishSchema(HealthyInstallResult(), CovenantHealthTransition.Bootstrap);

        CovenantAvailabilitySnapshot before = availability.PublishCanonicalState(
            DatasetGeneration,
            canonicalSequence: 11,
            coreCampaignDeletionSequence: 2,
            rebuildRequired: false,
            CovenantHealthTransition.CanonicalMutation);

        CovenantAvailabilitySnapshot after = availability.PublishFeatureEnabled(true);

        CovenantAvailabilitySnapshot expected = before with
        {

            Generation = before.Generation + 1,

            FeatureEnabled = true,

            LastHealthTransition = CovenantHealthTransition.FeatureConfiguration,

        };

        Assert.Equal(expected, after);

        Assert.True(after.FeatureEnabled);

        Assert.Equal(before.Generation + 1, after.Generation);

    }

    /// <summary>
    /// The accelerator's applied tuple, epoch, synchronization state, and rebuild flag move together
    /// or not at all, and the canonical side is left untouched.
    /// </summary>
    /// <remarks>
    /// Atomicity is the point rather than the individual values. A reader that saw the applied
    /// sequence advance before the epoch, or the synchronization state turn Synchronized before the
    /// rebuild flag cleared, would serve text belonging to a Campaign that has since been deleted.
    /// </remarks>
    [Fact]
    public void Projection_publication_updates_applied_tuple_epoch_and_rebuild_state_atomically()
    {

        CovenantAvailability availability = new();

        _ = availability.PublishSchema(HealthyInstallResult(), CovenantHealthTransition.Bootstrap);

        CovenantAvailabilitySnapshot before = availability.PublishCanonicalState(
            DatasetGeneration,
            canonicalSequence: 90,
            coreCampaignDeletionSequence: 5,
            rebuildRequired: true,
            CovenantHealthTransition.CanonicalMutation);

        Assert.Null(before.AppliedSequence);

        Assert.True(before.RebuildRequired);

        CovenantAvailabilitySnapshot after = availability.PublishAcceleratorState(
            appliedDatasetGeneration: DatasetGeneration,
            appliedSequence: 90,
            appliedCampaignDeletionSequence: 5,
            acceleratorEpoch: 12,
            CovenantFtsSynchronizationState.Synchronized,
            rebuildRequired: false,
            CovenantHealthTransition.AcceleratorRebuild);

        CovenantAvailabilitySnapshot expected = before with
        {

            Generation = before.Generation + 1,

            AppliedDatasetGeneration = DatasetGeneration,

            AppliedSequence = 90,

            AppliedCampaignDeletionSequence = 5,

            AcceleratorEpoch = 12,

            FtsSynchronization = CovenantFtsSynchronizationState.Synchronized,

            RebuildRequired = false,

            LastHealthTransition = CovenantHealthTransition.AcceleratorRebuild,

        };

        Assert.Equal(expected, after);

        // The canonical cursors belong to another publisher and must be exactly where it left them.
        Assert.Equal(90L, after.CanonicalSequence);

        Assert.Equal(5L, after.CoreCampaignDeletionSequence);

        Assert.Equal(DatasetGeneration, after.DatasetGeneration);

    }

    /// <summary>
    /// The asymmetry is the point: a canonical failure disables Covenant, an accelerator failure only
    /// degrades search.
    /// </summary>
    /// <remarks>
    /// Canonical holds the authoritative rows, so nothing may read Covenant without it. The
    /// accelerator only makes inspection faster, and the canonical fallback still answers every query
    /// it would have served, so marking it Unavailable would disable a capability that still works.
    /// The FTS synchronization state has to fall to Unavailable in the same breath, because a Dirty
    /// index is one that exists and is behind, not one that was never installed.
    /// </remarks>
    [Fact]
    public void Canonical_failure_marks_canonical_unavailable_and_accelerator_failure_marks_search_degraded()
    {

        CovenantAvailability canonicalFailed = new();

        CovenantAvailabilitySnapshot canonical = canonicalFailed.PublishSchema(
            new GrimoireSchemaInstallResult(
                HealthyTier(GrimoireSchemaTransactionTier.Core, installed: CanonicalFingerprint),
                FailedTier(
                    GrimoireSchemaTransactionTier.CovenantCanonical,
                    GrimoireSchemaTierHealth.Unavailable),
                FailedTier(
                    GrimoireSchemaTransactionTier.CovenantAccelerator,
                    GrimoireSchemaTierHealth.DependencyUnavailable)),
            CovenantHealthTransition.Bootstrap);

        Assert.Equal(CovenantCapabilityState.Unavailable, canonical.Canonical);

        Assert.Null(canonical.CanonicalSchemaVersion);

        Assert.Null(canonical.CanonicalInstalledFingerprint);

        Assert.Equal("Grimoire.Schema.Unavailable", canonical.CanonicalDiagnosticCode);

        Assert.Equal(
            "Grimoire.Schema.DependencyUnavailable",
            canonical.AcceleratorDiagnosticCode);

        Assert.Equal(CovenantFtsSynchronizationState.Unavailable, canonical.FtsSynchronization);

        CovenantAvailability acceleratorFailed = new();

        CovenantAvailabilitySnapshot accelerator = acceleratorFailed.PublishSchema(
            new GrimoireSchemaInstallResult(
                HealthyTier(GrimoireSchemaTransactionTier.Core, installed: CanonicalFingerprint),
                HealthyTier(
                    GrimoireSchemaTransactionTier.CovenantCanonical,
                    installed: CanonicalFingerprint),
                FailedTier(
                    GrimoireSchemaTransactionTier.CovenantAccelerator,
                    GrimoireSchemaTierHealth.Unavailable)),
            CovenantHealthTransition.Bootstrap);

        Assert.Equal(CovenantCapabilityState.Healthy, accelerator.Canonical);

        Assert.Equal(SchemaVersion, accelerator.CanonicalSchemaVersion);

        Assert.Equal(CovenantCapabilityState.Degraded, accelerator.Accelerator);

        Assert.Null(accelerator.AcceleratorSchemaVersion);

        Assert.Equal(CovenantFtsSynchronizationState.Unavailable, accelerator.FtsSynchronization);

        Assert.Equal("Grimoire.Schema.Unavailable", accelerator.AcceleratorDiagnosticCode);

        Assert.Null(accelerator.CanonicalDiagnosticCode);

    }

    /// <summary>
    /// Publications that commit at the same moment produce distinct generations, never one that
    /// silently overwrites the other.
    /// </summary>
    /// <remarks>
    /// A plain swap would let two racing publishers read the same generation, compute the same
    /// successor, and both write it, leaving a counter that advanced once for two publications. A
    /// turn holding the earlier value would then prove availability had not changed when it had. The
    /// final generation is therefore asserted to equal the publication count plus the initial value,
    /// and the returned generations to be all distinct, which is the same property from both ends.
    /// </remarks>
    [Fact]
    public void Concurrent_publications_produce_distinct_monotonic_generations()
    {

        const int Publications = 512;

        CovenantAvailability availability = new();

        long initial = availability.Current.Generation;

        ConcurrentBag<long> generations = [];

        _ = Parallel.For(
            0,
            Publications,
            index => generations.Add(availability.PublishFeatureEnabled(index % 2 == 0).Generation));

        Assert.Equal(Publications, generations.Count);

        Assert.Equal(Publications, generations.Distinct().Count());

        Assert.Equal(initial + Publications, availability.Current.Generation);

        Assert.Equal(initial + Publications, generations.Max());

    }

    /// <summary>
    /// Availability fails closed before bootstrap runs. Nothing has installed a schema yet, so both
    /// tiers are unavailable, search cannot be trusted, a rebuild is owed, and the feature is off.
    /// </summary>
    /// <remarks>
    /// This is the state a gate reads if it runs before the bootstrapper publishes, and it is the
    /// state a default-initialized field would produce if the enums allowed a zero member. Starting
    /// healthy and being corrected afterwards would open a window in which a Covenant path ran
    /// against a database that had no Covenant tables in it.
    /// </remarks>
    [Fact]
    public void Initial_snapshot_is_unavailable_and_rebuild_required()
    {

        CovenantAvailability availability = new();

        CovenantAvailabilitySnapshot snapshot = availability.Current;

        Assert.Equal(CovenantCapabilityState.Unavailable, snapshot.Canonical);

        Assert.Equal(CovenantCapabilityState.Unavailable, snapshot.Accelerator);

        Assert.Equal(CovenantFtsSynchronizationState.Unavailable, snapshot.FtsSynchronization);

        Assert.True(snapshot.RebuildRequired);

        Assert.False(snapshot.FeatureEnabled);

        Assert.Null(snapshot.CanonicalSchemaVersion);

        Assert.Null(snapshot.AcceleratorSchemaVersion);

        Assert.Null(snapshot.DatasetGeneration);

        Assert.Null(snapshot.AppliedDatasetGeneration);

        Assert.Null(snapshot.AppliedSequence);

        Assert.Null(snapshot.AppliedCampaignDeletionSequence);

        Assert.Equal(0L, snapshot.CanonicalSequence);

        Assert.Equal(0L, snapshot.CoreCampaignDeletionSequence);

        Assert.Equal(0UL, snapshot.AcceleratorEpoch);

        Assert.Equal(CovenantHealthTransition.Bootstrap, snapshot.LastHealthTransition);

        Assert.Equal(1L, snapshot.Generation);

    }

    /// <summary>
    /// The all-healthy install outcome the bootstrap path publishes on a fresh database.
    /// </summary>
    private static GrimoireSchemaInstallResult HealthyInstallResult() =>
        new(
            HealthyTier(GrimoireSchemaTransactionTier.Core, CanonicalFingerprint),
            HealthyTier(GrimoireSchemaTransactionTier.CovenantCanonical, CanonicalFingerprint),
            HealthyTier(GrimoireSchemaTransactionTier.CovenantAccelerator, AcceleratorFingerprint));

    private static GrimoireSchemaTierInstallResult HealthyTier(
        GrimoireSchemaTransactionTier tier,
        string installed) =>
        new(
            tier,
            SchemaVersion,
            GrimoireSchemaTierHealth.Healthy,
            SourceFingerprint,
            installed,
            DiagnosticCode: null);

    /// <summary>
    /// A failed tier carries no installed-catalog fingerprint and a closed, content-free diagnostic
    /// code, which is the shape the installer produces for every non-healthy outcome.
    /// </summary>
    private static GrimoireSchemaTierInstallResult FailedTier(
        GrimoireSchemaTransactionTier tier,
        GrimoireSchemaTierHealth health) =>
        new(
            tier,
            SchemaVersion,
            health,
            SourceFingerprint,
            InstalledCatalogFingerprint: null,
            $"Grimoire.Schema.{health}");

}
