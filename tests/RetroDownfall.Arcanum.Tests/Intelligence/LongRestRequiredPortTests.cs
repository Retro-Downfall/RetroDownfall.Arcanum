using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.LongRest;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class LongRestOptionalPortRefusalTests
{
    // Preservation of the existing eligible-live fail-closed branch.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_live_coordinator_or_epoch_terminalizes_claim_without_requesting_maintenance(bool missingEpoch)
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        CampaignRollupTurnSnapshotTests.ProbeStore store = new();

        CampaignRollupTurnSnapshotTests.AcceptingClaims claims = new();

        LongRestConfigurationEpoch epoch = new(options, Guid.NewGuid());

        CampaignRollupTurnPreparer preparer = new(store, options,
            new CampaignRollupTurnSnapshotTests.Resolver(), null!,
            claimBegin: new CampaignRollupTurnSnapshotTests.InputStore(), claims: claims,
            authoritySnapshot: new CampaignRollupTurnSnapshotTests.Authority(),
            longRest: missingEpoch ? new UnreachedCoordinator() : null,
            longRestConfiguration: missingEpoch ? null : epoch);

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, CampaignRollupTurnSnapshotTests.Campaign,
            InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", SessionId: Guid.NewGuid()), invocation, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, result.Error.Code);

        Assert.Equal(0, store.Reads);

        Assert.Equal(1, claims.Completions);
    }

    [Theory]
    [InlineData("maintenance")]
    [InlineData("discovery")]
    public async Task Missing_protected_ports_refuse_before_request_disclosure_or_send(string missing)
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f =
            await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        CampaignRollupTurnSnapshotTests.ProbeStore store = new();

        CampaignRollupTurnSnapshotTests.AcceptingClaims claims = new();

        long disclosures = (long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))!;

        CampaignRollupTurnPreparer preparer = new(store, f.Options,
            new CampaignRollupTurnSnapshotTests.Resolver(), f.Gate,
            claimBegin: new CampaignRollupTurnSnapshotTests.InputStore(), claims: claims,
            authoritySnapshot: new CovenantAuthoritySnapshotProvider(f.Runtime),
            longRest: new UnreachedCoordinator(),
            longRestConfiguration: f.Epoch,
            longRestMaintenance: missing == "maintenance" ? null : f.Maintenance,
            longRestDiscovery: missing == "discovery" ? null : f.H.Store);

        Assert.True(f.Invocation.CanReadCovenant);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", SessionId: Guid.NewGuid()), f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, result.Error.Code);

        Assert.Equal(0, store.Reads);

        Assert.Equal(1, claims.Completions);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(disclosures, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));
    }

    private sealed class UnreachedCoordinator : ILongRestSweepCoordinator
    {
        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct) =>
            throw new InvalidOperationException("Missing required live port must refuse before maintenance dispatch.");

        public Task<Result<LongRunningOperationDto>> RequestAsync(
            CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct) =>
            throw new InvalidOperationException("Missing required live port must refuse before partition dispatch.");

        public Task<Result<LongRestSweepProgress>> ResumeAsync(
            Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            throw new InvalidOperationException("Missing required live port must refuse before provider work.");
    }
}
