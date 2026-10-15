using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The capability term a Long Rest fence carries is the Covenant capability state the sweep's protected reads stand on, not the counter
// every availability publication moves. These pin which facts move it and which publications leave it alone.
public sealed class LongRestCapabilityStateTests
{
    private static readonly Guid Dataset = new("11111111-1111-4111-8111-111111111111");

    private static CovenantAvailabilitySnapshot Baseline() => new(
        Generation: 7,
        FeatureEnabled: true,
        Canonical: CovenantCapabilityState.Healthy,
        CanonicalSchemaVersion: 3,
        CanonicalInstalledFingerprint: "canonical-fingerprint",
        Accelerator: CovenantCapabilityState.Healthy,
        AcceleratorSchemaVersion: 2,
        AcceleratorInstalledFingerprint: "accelerator-fingerprint",
        DatasetGeneration: Dataset,
        CanonicalSequence: 40,
        CoreCampaignDeletionSequence: 5,
        AppliedDatasetGeneration: Dataset,
        AppliedSequence: 40,
        AppliedCampaignDeletionSequence: 5,
        AcceleratorEpoch: 9,
        FtsSynchronization: CovenantFtsSynchronizationState.Synchronized,
        RebuildRequired: false,
        LastHealthTransition: CovenantHealthTransition.Bootstrap,
        CanonicalDiagnosticCode: null,
        AcceleratorDiagnosticCode: null);

    [Fact]
    public void The_state_is_a_positive_odd_number_and_the_same_snapshot_always_gives_the_same_one()
    {
        long state = LongRestCapabilityState.Of(Baseline());

        Assert.True(state >= 1);

        Assert.Equal(1L, state & 1L);

        Assert.Equal(state, LongRestCapabilityState.Of(Baseline()));
    }

    [Fact]
    public void The_encoding_is_pinned()
    {
        // A change to the encoding moves every stored fence's capability term, so it is a version change and not an edit. The value is
        // that of an independent implementation: SHA-256 over the domain string, the two state bytes and the three presence-tagged
        // facts, read as a big-endian signed 64-bit prefix, masked to 63 bits and made odd.
        Assert.Equal(5_327_692_441_172_021_665L, LongRestCapabilityState.Of(Baseline()));
    }

    public static TheoryData<string> PublicationsThatChangeNoCapability() =>
    [
        "generation",
        "canonical-sequence",
        "campaign-deletion-sequence",
        "applied-tuple",
        "accelerator-epoch",
        "synchronization",
        "rebuild-required",
        "transition",
        "accelerator-state",
        "diagnostic-code",
    ];

    [Theory]
    [MemberData(nameof(PublicationsThatChangeNoCapability))]
    public void A_publication_that_changes_no_capability_fact_leaves_the_state_alone(string publication)
    {
        CovenantAvailabilitySnapshot baseline = Baseline();

        CovenantAvailabilitySnapshot published = publication switch
        {
            "generation" => baseline with { Generation = baseline.Generation + 1 },
            "canonical-sequence" => baseline with { Generation = baseline.Generation + 1, CanonicalSequence = baseline.CanonicalSequence + 1 },
            "campaign-deletion-sequence" => baseline with { CoreCampaignDeletionSequence = baseline.CoreCampaignDeletionSequence + 1 },
            "applied-tuple" => baseline with { AppliedSequence = 41, AppliedCampaignDeletionSequence = 6, AppliedDatasetGeneration = null },
            "accelerator-epoch" => baseline with { AcceleratorEpoch = baseline.AcceleratorEpoch + 1 },
            "synchronization" => baseline with { FtsSynchronization = CovenantFtsSynchronizationState.Dirty },
            "rebuild-required" => baseline with { RebuildRequired = true },
            "transition" => baseline with { LastHealthTransition = CovenantHealthTransition.OwnerCleanup },
            "accelerator-state" => baseline with { Accelerator = CovenantCapabilityState.Degraded, AcceleratorSchemaVersion = null },
            "diagnostic-code" => baseline with { AcceleratorDiagnosticCode = "accelerator.rebuild" },
            _ => throw new ArgumentOutOfRangeException(nameof(publication)),
        };

        Assert.Equal(LongRestCapabilityState.Of(baseline), LongRestCapabilityState.Of(published));
    }

    public static TheoryData<string> ChangesToACapabilityFact() =>
    [
        "feature",
        "canonical-health",
        "canonical-unavailable",
        "schema-version",
        "schema-version-absent",
        "fingerprint",
        "fingerprint-absent",
        "dataset",
        "dataset-absent",
    ];

    [Theory]
    [MemberData(nameof(ChangesToACapabilityFact))]
    public void A_change_to_a_fact_the_sweeps_reads_stand_on_moves_the_state(string change)
    {
        CovenantAvailabilitySnapshot baseline = Baseline();

        CovenantAvailabilitySnapshot changed = change switch
        {
            "feature" => baseline with { FeatureEnabled = false },
            "canonical-health" => baseline with { Canonical = CovenantCapabilityState.Degraded },
            "canonical-unavailable" => baseline with { Canonical = CovenantCapabilityState.Unavailable },
            "schema-version" => baseline with { CanonicalSchemaVersion = 4 },
            "schema-version-absent" => baseline with { CanonicalSchemaVersion = null },
            "fingerprint" => baseline with { CanonicalInstalledFingerprint = "another-fingerprint" },
            "fingerprint-absent" => baseline with { CanonicalInstalledFingerprint = null },
            "dataset" => baseline with { DatasetGeneration = Guid.NewGuid() },
            "dataset-absent" => baseline with { DatasetGeneration = null },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Assert.NotEqual(LongRestCapabilityState.Of(baseline), LongRestCapabilityState.Of(changed));
    }

    [Fact]
    public void An_absent_fact_is_not_the_same_as_an_empty_one()
    {
        CovenantAvailabilitySnapshot baseline = Baseline();

        Assert.NotEqual(
            LongRestCapabilityState.Of(baseline with { CanonicalInstalledFingerprint = null }),
            LongRestCapabilityState.Of(baseline with { CanonicalInstalledFingerprint = string.Empty }));

        Assert.NotEqual(
            LongRestCapabilityState.Of(baseline with { CanonicalSchemaVersion = null }),
            LongRestCapabilityState.Of(baseline with { CanonicalSchemaVersion = 0 }));
    }

    [Fact]
    public void A_null_snapshot_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => LongRestCapabilityState.Of(null!));
    }
}
