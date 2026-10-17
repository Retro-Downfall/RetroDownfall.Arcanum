using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class LongRestMissingSnapshotEpochTests
{
    // Existing behavior preservation; this is not a runtime bug RED.
    [Fact]
    public async Task Stamped_snapshot_without_epoch_refuses_before_current_source_reads()
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        CampaignRollupTurnSnapshotTests.ProbeStore store = new();

        await using CampaignRollupTurnSnapshot snapshot = new(null, CanonicalCampaignContext.GlobalOnly,
            new PingRequest("Continue.", SessionId: Guid.NewGuid()), store,
            new TestOptionsMonitor<ArcanumSettings>(settings), new UnreachedResolver(), null,
            requiresRollups: false, longRestStamp: new(Guid.NewGuid(), 1, new CovenantDigest(new byte[32])),
            longRestConfiguration: null);

        Result result = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(0, store.Reads);
    }

    private sealed class UnreachedResolver : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(
            CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Missing stamped epoch must refuse before source resolution.");
    }
}
