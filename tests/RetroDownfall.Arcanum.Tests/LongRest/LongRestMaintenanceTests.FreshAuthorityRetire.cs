using System.Net;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Fresh-Authority Retire. A protected sweep parked with a fault that left its captured generation unable to complete can never be
// readmitted by anything, so a fresh request that carries the live turn authority of a Session turn for that partition retires it
// (owner-guarded, evidence preserved, inside the request's own admitted immediate transaction) and starts a new generation. A request
// without that authority, a park that needs an operator, and a contested owner all keep their old outcomes and write nothing.
public sealed partial class LongRestMaintenanceTests
{
    /// <summary>
    /// Runs a protected sweep to a real, paid-and-failed park (a disclosure receipt and a settled effect row exist), then stamps the
    /// park with the given code, which is the fault that stranded the generation.
    /// </summary>
    private static async Task<(long Receipts, EffectRow[] Effects)> ParkProtectedWithEvidenceAsync(MaintenanceFixture f, string code)
    {
        f.Handler.Status = HttpStatusCode.InternalServerError;

        LongRestSweepProgress failed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, failed.DeferredReason);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET TerminalErrorCode = $code WHERE Id = $operation AND State = $parked;",
            ("$code", code), ("$operation", f.Operation.ToString("N")), ("$parked", (int)LongRunningOperationState.ReconciliationRequired));

        Assert.Equal(code, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);

        long receipts = (long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))!;

        Assert.Equal(1L, receipts);

        return (receipts, await EffectRowsAsync(f));
    }

    [Theory]
    [InlineData("LongRest.StaleInput")]
    [InlineData("LongRest.StaleFence")]
    [InlineData("LongRest.CurrentnessLost")]
    [InlineData("LongRest.ConfigurationChanged")]
    public async Task A_fresh_request_with_live_authority_retires_a_protected_restart_generation_park_and_starts_a_new_generation(string code)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        (long receipts, EffectRow[] effects) = await ParkProtectedWithEvidenceAsync(f, code);

        long generations = (long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"))!;

        Result<LongRunningOperationDto> replaced = await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None);

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Code : null);

        Assert.NotEqual(f.Operation, replaced.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, replaced.Value.State);

        // Retired through the Abandoned path under the fault's own code: no lease, a completion instant, nothing resumable.
        LongRunningOperation retired = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(code, retired.TerminalErrorCode);

        Assert.Null(retired.LeaseOwner);

        Assert.Null(retired.LeaseExpiresAt);

        Assert.NotNull(retired.CompletedAt);

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7, 9);"));

        // Evidence is preserved: the receipt, the settled effect row and the generation are all still there, exactly as they were.
        Assert.Equal(receipts, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(effects, await EffectRowsAsync(f));

        Assert.Equal(generations, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));

        // The replacement is a new generation, and it completes under a capability issued for it.
        f.Handler.Status = HttpStatusCode.OK;

        SummaryOrDecision(f);

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(
            replaced.Value.Id, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        LongRestSweepProgress completed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(replaced.Value.Id, capability, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, completed.Phase);

        Assert.NotEqual(generations, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));
    }

    [Theory]
    [InlineData("LongRest.StaleInput")]
    [InlineData("LongRest.StaleFence")]
    [InlineData("LongRest.CurrentnessLost")]
    [InlineData("LongRest.ConfigurationChanged")]
    public async Task A_fresh_request_without_authority_still_finds_the_protected_restart_generation_park_and_writes_nothing(string code)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedWithEvidenceAsync(f, code);

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
    }

    [Theory]
    [InlineData("LongRest.ProviderRejected")]
    [InlineData("LongRest.TransportUnknown")]
    [InlineData("LongRest.ModelContextTooSmall")]
    [InlineData("LongRest.InvalidOutput")]
    [InlineData("long_rest.readmit_exhausted")]
    [InlineData("LongRest.SomethingNobodyClassified")]
    public async Task Live_authority_does_not_free_a_protected_park_that_needs_an_operator(string code)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedWithEvidenceAsync(f, code);

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);

        Assert.Contains(f.Operation.ToString("D"), request.Error.Message, StringComparison.Ordinal);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
    }

    [Fact]
    public async Task Live_authority_returns_a_protected_park_that_a_fresh_request_can_readmit_unchanged()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedWithEvidenceAsync(f, LongRestParkCodes.AuthorizedRequestRequired);

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        // The authority is for the restart-generation retire only: a park that a fresh capability readmits keeps its operation.
        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None)).Id);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
    }

    [Fact]
    public async Task A_contested_owner_makes_the_authority_bearing_request_OperationBusy_and_writes_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedWithEvidenceAsync(f, ErrorCodes.LongRest.StaleFence);

        Assert.True(f.Ownership.TryClaim(f.Operation, out Guid token));

        try
        {
            string before = await ParkedSnapshotAsync(f);

            long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

            Result<LongRunningOperationDto> busy = await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None);

            Assert.True(busy.IsFailure);

            Assert.Equal(ErrorCodes.LongRest.OperationBusy, busy.Error.Code);

            Assert.Equal(before, await ParkedSnapshotAsync(f));

            Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
        }
        finally
        {
            f.Ownership.Release(f.Operation, token);
        }

        // Once the other executor lets go, the same request retires the park.
        Assert.NotEqual(f.Operation, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task An_authority_that_was_released_frees_nothing_and_writes_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedWithEvidenceAsync(f, ErrorCodes.LongRest.StaleFence);

        CovenantTurnLease released = (await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None)).Value;

        await released.DisposeAsync();

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, released, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, request.Error.Code);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
    }

    [Fact]
    public async Task An_authority_leased_for_another_scope_frees_nothing_and_writes_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedWithEvidenceAsync(f, ErrorCodes.LongRest.StaleFence);

        // A live lease, but for a Campaign the parked Global partition does not belong to.
        CanonicalCampaignContext other = CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(Guid.NewGuid()), 1, 1, null, null);

        Result<CovenantTurnLease> foreign = await f.Gate.AcquireTurnAsync(other, CancellationToken.None);

        Assert.True(foreign.IsSuccess, foreign.IsFailure ? foreign.Error.Code : null);

        await using CovenantTurnLease lease = foreign.Value;

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, lease, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, request.Error.Code);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
    }
}
