using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestLiveFenceTests
{
    [Fact]
    public async Task Initial_capture_uses_actual_composite_dataset_and_admitted_highwater_without_writes()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("first", "observation");

        using CovenantRuntimeGenerationProvider runtime = new();

        Guid dataset = Guid.NewGuid();

        runtime.PublishAvailability(current => current with { DatasetGeneration = dataset, Canonical = CovenantCapabilityState.Healthy, FeatureEnabled = false });

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        LongRestConfigurationEpochTests.NotifyingOptions options = new(LongRestConfigurationEpochTests.Settings());

        using LongRestConfigurationEpoch epoch = new(options, Guid.NewGuid());

        NoCampaigns campaigns = new();

        LongRestLiveFenceReader reader = new(h.Saga.Context, runtime, gate, epoch, campaigns, campaigns);

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        LongRestSweepFence first = LongRestSweepHarness.Require(await reader.CaptureAsync(CanonicalCampaignContext.GlobalOnly, epoch.Capture().Stamp, null, CancellationToken.None));

        Assert.Equal(dataset, first.DatasetGeneration);

        Assert.Equal(runtime.Current.RuntimeAuthorityGeneration, first.RuntimeAuthorityGeneration);

        Assert.Equal(LongRestCapabilityState.Of(runtime.Current.Availability), first.CapabilityGeneration);

        Assert.Equal(gate.CurrentGeneration, first.AdmissionGeneration);

        Assert.Equal(await h.ScalarAsync("SELECT max(InsertionSequence) FROM long_rest_sources;"), first.InsertionHighWatermark);

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));

        await h.SeedAsync("second", "later observation");

        LongRestSweepFence resumed = LongRestSweepHarness.Require(await reader.CaptureAsync(CanonicalCampaignContext.GlobalOnly, epoch.Capture().Stamp, first.InsertionHighWatermark, CancellationToken.None));

        Assert.Equal(first.InsertionHighWatermark, resumed.InsertionHighWatermark);

        Assert.Equal(first, resumed);
    }

    [Fact]
    public async Task A_publication_that_changes_no_capability_fact_leaves_the_fence_equal_and_one_that_does_changes_it()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("first", "observation");

        using CovenantRuntimeGenerationProvider runtime = new();

        runtime.PublishAvailability(current => current with { DatasetGeneration = Guid.NewGuid(), Canonical = CovenantCapabilityState.Healthy, FeatureEnabled = true });

        LongRestConfigurationEpochTests.NotifyingOptions options = new(LongRestConfigurationEpochTests.Settings());

        using LongRestConfigurationEpoch epoch = new(options, Guid.NewGuid());

        NoCampaigns campaigns = new();

        LongRestLiveFenceReader reader = new(h.Saga.Context, runtime, new GrimoireConnectionAdmissionGate(TimeProvider.System), epoch, campaigns, campaigns);

        LongRestSweepFence first = LongRestSweepHarness.Require(await reader.CaptureAsync(CanonicalCampaignContext.GlobalOnly, epoch.Capture().Stamp, null, CancellationToken.None));

        // A Covenant write's new sequence, then a maintenance pass that changed nothing: both republish, neither changes a capability fact.
        runtime.PublishAvailability(current => current with { CanonicalSequence = current.CanonicalSequence + 1, LastHealthTransition = CovenantHealthTransition.CanonicalMutation });

        runtime.PublishAvailability(current => current with { LastHealthTransition = CovenantHealthTransition.OwnerCleanup });

        Assert.True(runtime.Current.Availability.Generation > 2);

        LongRestSweepFence republished = LongRestSweepHarness.Require(await reader.CaptureAsync(CanonicalCampaignContext.GlobalOnly, epoch.Capture().Stamp, first.InsertionHighWatermark, CancellationToken.None));

        Assert.Equal(first, republished);

        Assert.True(reader.IsRequestFenceCurrent(first));

        runtime.PublishAvailability(current => current with { FeatureEnabled = false });

        LongRestSweepFence switched = LongRestSweepHarness.Require(await reader.CaptureAsync(CanonicalCampaignContext.GlobalOnly, epoch.Capture().Stamp, first.InsertionHighWatermark, CancellationToken.None));

        Assert.NotEqual(first, switched);

        Assert.NotEqual(first.CapabilityGeneration, switched.CapabilityGeneration);

        Assert.False(reader.IsRequestFenceCurrent(first));
    }

    [Theory]
    [InlineData("dataset")]
    [InlineData("canonical")]
    [InlineData("configuration")]
    public async Task Missing_actual_lineage_or_current_configuration_defers_without_writes(string unavailable)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        using CovenantRuntimeGenerationProvider runtime = new();

        runtime.PublishAvailability(current => current with { DatasetGeneration = unavailable == "dataset" ? null : Guid.NewGuid(),
            Canonical = unavailable == "canonical" ? CovenantCapabilityState.Unavailable : CovenantCapabilityState.Healthy });

        LongRestConfigurationEpochTests.NotifyingOptions options = new(LongRestConfigurationEpochTests.Settings());

        using LongRestConfigurationEpoch epoch = new(options, Guid.NewGuid());

        LongRestConfigurationStamp stamp = epoch.Capture().Stamp;

        if (unavailable == "configuration")
        {
            var changed = LongRestConfigurationEpochTests.Settings();

            changed.DefaultModel = "other";

            options.Set(changed);

            options.Set(LongRestConfigurationEpochTests.Settings());
        }

        NoCampaigns campaigns = new();

        LongRestLiveFenceReader reader = new(h.Saga.Context, runtime, new GrimoireConnectionAdmissionGate(TimeProvider.System), epoch, campaigns, campaigns);

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await reader.CaptureAsync(CanonicalCampaignContext.GlobalOnly, stamp, null, CancellationToken.None)).IsFailure);

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));
    }

    internal sealed class NoCampaigns : ICampaignAvailabilityReader, ICampaignPathIdentityReader
    {
        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken) => ValueTask.FromResult(Result<long?>.Success(null));

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken) => ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(null));

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken) => ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(null));
    }
}
