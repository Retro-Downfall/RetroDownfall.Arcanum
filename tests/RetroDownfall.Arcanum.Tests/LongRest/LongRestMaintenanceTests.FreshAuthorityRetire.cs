using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Fresh-Authority Retire. A protected sweep parked with a fault that left its captured generation unable to complete can never be
// readmitted by anything, so a fresh request that carries the live turn authority of a Session turn for that partition retires it
// (owner-guarded, evidence preserved, inside the request's own admitted immediate transaction) and starts a new generation. A request
// without that authority, a park that needs an operator, and a contested owner all keep their old outcomes and write nothing. The parks
// that can be retired are exactly the restart-generation class of LongRestFaultPolicy, the one table every Long Rest rule reads: the four
// faults a run can end with and the two retirement markers (stale configuration, never started), each pinned both ways below.
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
    [InlineData(LongRestParkCodes.StaleConfiguration)]
    [InlineData(LongRestParkCodes.NotStarted)]
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
    [InlineData(LongRestParkCodes.StaleConfiguration)]
    [InlineData(LongRestParkCodes.NotStarted)]
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

    // A protected park that waits for a fresh Session request, whose generation can no longer resume under the installation of a later boot,
    // is retired by the next turn that carries authority. Three things other than the fence keep a generation from resuming, and each strands
    // the partition the same way: a capability for the park is refused on every turn, so the park is handed back unchanged on every authorized
    // one, and every Session turn that needs the partition ends until an operator finds the operation and cancels it. A park that is still
    // resumable is the same park, and is handed back.

    /// <summary>Rewrites the stored recipe of the fixture's generation the way another build of the code would have written it.</summary>
    private static async Task RewriteStoredRecipeAsync(MaintenanceFixture f, Func<LongRestRecipeIdentity, LongRestRecipeIdentity> change)
    {
        string json = (string)(await f.H.ScalarAsync($"SELECT RecipeJson FROM long_rest_discovery_generations WHERE OperationId = '{f.Operation:N}';"))!;

        LongRestRecipeIdentity recipe = JsonSerializer.Deserialize(json, LongRestJsonContext.Default.LongRestRecipeIdentity)!;

        LongRestRecipeIdentity changed = change(recipe);

        Assert.NotEqual(recipe, changed);

        string rewritten = JsonSerializer.Serialize(changed, LongRestJsonContext.Default.LongRestRecipeIdentity);

        // A recipe the code in force reads has the digest the code in force gives it. One it no longer reads (a fixed bound moved) has no digest
        // now, and keeps the one the build that stored it gave it, which the code in force does not give the recipe it owns.
        Result<CovenantDigest> digest = LongRestDiscoveryHash.Recipe(changed);

        await f.H.ExecuteAsync("UPDATE long_rest_discovery_generations SET RecipeJson = $json, RecipeDigest = $digest WHERE OperationId = $operation;",
            ("$json", rewritten), ("$digest", digest.IsSuccess ? digest.Value.Bytes : SHA256.HashData(Encoding.UTF8.GetBytes(rewritten))),
            ("$operation", f.Operation.ToString("N")));
    }

    /// <summary>The park a turn is handed after a restart: authorized_request_required, with a paid and failed unit behind it.</summary>
    private static async Task<(long Receipts, EffectRow[] Effects)> ParkProtectedAcrossABootAsync(MaintenanceFixture f)
    {
        (long receipts, EffectRow[] effects) = await ParkProtectedWithEvidenceAsync(f, LongRestParkCodes.AuthorizedRequestRequired);

        await f.RebootAsync();

        return (receipts, effects);
    }

    /// <summary>
    /// Pins the strand and its release: no capability can be issued for the park, a request without authority is handed it back unchanged with
    /// no write, and the next request that carries live authority retires it as stale input, keeps its evidence, and starts a generation
    /// that completes.
    /// </summary>
    private static async Task AssertTheNextAuthorizedTurnRetiresTheParkAsync(MaintenanceFixture f, CovenantTurnLease authority, long receipts, EffectRow[] effects,
        bool scriptTheProvider = true)
    {
        Assert.True((await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, authority, CancellationToken.None)).IsFailure);

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        long generations = (long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"))!;

        Result<LongRunningOperationDto> unauthorized = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(unauthorized.IsSuccess, unauthorized.IsFailure ? unauthorized.Error.Code : null);

        Assert.Equal(f.Operation, unauthorized.Value.Id);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);

        Result<LongRunningOperationDto> replaced = await f.Coordinator.RequestAsync(f.Scope, f.Partition, authority, CancellationToken.None);

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Code : null);

        Assert.NotEqual(f.Operation, replaced.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, replaced.Value.State);

        LongRunningOperation retired = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, retired.TerminalErrorCode);

        Assert.Null(retired.LeaseOwner);

        Assert.NotNull(retired.CompletedAt);

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7, 9);"));

        Assert.Equal(receipts, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(effects, await EffectRowsAsync(f));

        Assert.Equal(generations, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));

        Assert.False(f.Ownership.IsClaimed(f.Operation));

        f.Handler.Status = HttpStatusCode.OK;

        // A scenario that scripted the provider for its own corpus keeps its script.
        if (scriptTheProvider) SummaryOrDecision(f);

        // A corpus of many leaves takes the turn more than one pass: each pass is issued its own capability, as a Session turn is.
        LongRestSweepProgress completed;

        int passes = 0;

        do
        {
            LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(
                replaced.Value.Id, f.Invocation, f.Claim, f.Scope, authority, CancellationToken.None));

            completed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(replaced.Value.Id, capability, CancellationToken.None));
        } while (completed.Phase != LongRestSweepPhase.Completed && ++passes < 200);

        Assert.True(completed.Phase == LongRestSweepPhase.Completed, $"{completed.Phase}: {completed.DeferredReason}");

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(replaced.Value.Id))!.State);
    }

    // A version the build moved, whether the stored recipe still parses under it (the scorer, the adjudication) or not (the pairing
    // arithmetic, a fixed bound, which the stored generation can then no longer be read under at all).
    [Theory]
    [InlineData("adjudication-version")]
    [InlineData("lexical-scorer-version")]
    [InlineData("pairing-version")]
    [InlineData("fixed-bound")]
    public async Task A_protected_park_captured_under_another_recipe_is_retired_by_the_next_authorized_turn(string change)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        (long receipts, EffectRow[] effects) = await ParkProtectedAcrossABootAsync(f);

        await RewriteStoredRecipeAsync(f, recipe => change switch
        {
            "adjudication-version" => recipe with { AdjudicationRecipeVersion = recipe.AdjudicationRecipeVersion + 1 },
            "lexical-scorer-version" => recipe with { LexicalScorerVersion = recipe.LexicalScorerVersion + 1 },
            "pairing-version" => recipe with { PairingRecipeVersion = recipe.PairingRecipeVersion - 1 },
            "fixed-bound" => recipe with { SourcePageSize = recipe.SourcePageSize + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        });

        // The recipe is the one thing about the park that moved: it is in a later boot, and its fence is the one a turn would resume it under.
        if (change is "adjudication-version" or "lexical-scorer-version")
        {
            LongRestSweepFence live = LongRestSweepHarness.Require(await f.Facts.CaptureAsync(f.Scope, f.Epoch.Capture().Stamp, null, CancellationToken.None));

            LongRestDiscoveryGeneration stored = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

            Assert.True(LongRestFenceCurrency.CanResume(stored.Fence, live));

            Assert.NotEqual(((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, stored.Recipe);
        }

        await AssertTheNextAuthorizedTurnRetiresTheParkAsync(f, f.Authority, receipts, effects);
    }

    // An upgrade that moves a version the stored generation was captured under, or a fixed bound, leaves every generation stored before it
    // unreadable whole, and retiring a generation does not need it whole: retirement reads only the columns of the generation's row, which
    // every statement that retires it is guarded by. So a sweep stored before such an upgrade is replaced by whichever request finds it,
    // by every route that ends it, and never strands the scope it holds. The two changes below are the ones a stored recipe cannot be
    // parsed under at all.

    /// <summary>Rewrites the stored recipe to one the code in force does not read: another pairing version, or another fixed bound.</summary>
    private static Task RewriteStoredRecipeToAnUnreadableOneAsync(MaintenanceFixture f, string change) => RewriteStoredRecipeAsync(f, recipe => change switch
    {
        "pairing-version" => recipe with { PairingRecipeVersion = recipe.PairingRecipeVersion - 1 },
        "fixed-bound" => recipe with { SourcePageSize = recipe.SourcePageSize + 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    });

    /// <summary>Moves the configuration value digest, the way an edit to a price does.</summary>
    private static void EditPricing(MaintenanceFixture f)
    {
        ArcanumSettings changed = LongRestConfigurationEpochTests.Settings();

        changed.Cost.Pricing.DefaultPricing!.InputPer1M = 13m;

        f.Options.Set(changed);
    }

    /// <summary>The generation is stored, and the code in force cannot read it: the question every resume entry asks of it is refused.</summary>
    private static async Task AssertTheStoredGenerationCannotBeReadAsync(MaintenanceFixture f)
    {
        Assert.Equal(1L, await f.H.ScalarAsync($"SELECT count(*) FROM long_rest_discovery_generations WHERE OperationId = '{f.Operation:N}';"));

        Result<LongRestDiscoveryGeneration?> read = await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, read.Error.Code);
    }

    /// <summary>The operation is terminal under the given code, its sweep and its generation are retired, and one new sweep owns the scope.</summary>
    private static async Task AssertTheSweepWasReplacedAsync(MaintenanceFixture f, Guid retiredOperation, string code, LongRunningOperationDto replacement)
    {
        Assert.NotEqual(retiredOperation, replacement.Id);

        Assert.Equal(LongRunningOperationState.Pending, replacement.State);

        LongRunningOperation retired = (await f.Operations.GetAsync(retiredOperation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(code, retired.TerminalErrorCode);

        Assert.Null(retired.LeaseOwner);

        Assert.NotNull(retired.CompletedAt);

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{retiredOperation:N}';"));

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE OperationId = '{retiredOperation:N}';"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7, 9);"));

        Assert.Equal((long)LongRestSweepPhase.Capture, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{replacement.Id:N}';"));

        Assert.False(f.Ownership.IsClaimed(retiredOperation));
    }

    // The clean twin: the worker readmits the sweep in a later boot, cannot read its generation, and abandons it as stale input. The sweep
    // row stays active until a request replaces it, and the request must be able to, or Long Rest for the scope would never run again.
    [Theory]
    [InlineData("pairing-version")]
    [InlineData("fixed-bound")]
    public async Task A_clean_sweep_the_worker_abandoned_because_its_generation_is_no_longer_readable_is_replaced_by_the_next_request(string change)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        Guid stale = f.Operation;

        await f.RebootAsync();

        await RewriteStoredRecipeToAnUnreadableOneAsync(f, change);

        await AssertTheStoredGenerationCannotBeReadAsync(f);

        RecoveredSweep recovered = await RecoverThenReadmitAsync(f, "unreadable-clean-recovery");

        Assert.Null(recovered.Fault);

        Assert.Equal(LongRunningOperationState.Abandoned, recovered.State);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, (await f.Operations.GetAsync(stale))!.TerminalErrorCode);

        // The worker left the sweep row active under the scope, with nothing it can ever do about it.
        Assert.Equal(1L, await f.H.ScalarAsync($"SELECT count(*) FROM long_rest_sweeps WHERE OperationId = '{stale:N}' AND PhaseCode NOT IN (6, 7, 9);"));

        Result<LongRunningOperationDto> replaced = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Code : null);

        await AssertTheSweepWasReplacedAsync(f, stale, ErrorCodes.LongRest.StaleInput, replaced.Value);
    }

    // A protected park whose generation the code in force cannot read, in an installation whose configuration value has also moved: it can
    // never resume, so the next request that carries live authority retires it as a stale configuration. Retiring it does not ask the
    // generation for anything but its row.
    [Theory]
    [InlineData("pairing-version")]
    [InlineData("fixed-bound")]
    public async Task A_protected_park_whose_generation_is_no_longer_readable_is_retired_as_a_stale_configuration_by_the_next_authorized_turn(string change)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        (long receipts, EffectRow[] effects) = await ParkProtectedAcrossABootAsync(f);

        await RewriteStoredRecipeToAnUnreadableOneAsync(f, change);

        EditPricing(f);

        await AssertTheStoredGenerationCannotBeReadAsync(f);

        Result<LongRunningOperationDto> replaced = await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None);

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Code : null);

        await AssertTheSweepWasReplacedAsync(f, f.Operation, LongRestParkCodes.StaleConfiguration, replaced.Value);

        // Evidence is preserved: the receipt, the settled effect row and the generation are all still there.
        Assert.Equal(receipts, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(effects, await EffectRowsAsync(f));

        Assert.Equal(1L, await f.H.ScalarAsync($"SELECT count(*) FROM long_rest_discovery_generations WHERE OperationId = '{f.Operation:N}';"));
    }

    // The documented remedy for a park is the operator's cancel. It settles the operation (Cancelling, then the reconciler abandons it), and
    // the sweep row stays active until a request replaces it: the same retirement, over a generation the code in force cannot read.
    [Theory]
    [InlineData("pairing-version")]
    [InlineData("fixed-bound")]
    public async Task A_protected_park_an_operator_cancelled_whose_generation_is_no_longer_readable_is_replaced_by_the_next_authorized_turn(string change)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        (long receipts, EffectRow[] effects) = await ParkProtectedAcrossABootAsync(f);

        await RewriteStoredRecipeToAnUnreadableOneAsync(f, change);

        EditPricing(f);

        await AssertTheStoredGenerationCannotBeReadAsync(f);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.True(await f.Operations.RequestCancellationAsync(f.Operation, parked.Revision, DateTimeOffset.UtcNow));

        Assert.Equal(LongRunningOperationState.Cancelling, (await f.Operations.GetAsync(f.Operation))!.State);

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], f.Clock,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        Assert.Equal(1, (await reconciler.ReconcileNowAsync("operator-cancelled-park")).Abandoned);

        Assert.Equal(LongRestParkCodes.Cancelled, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);

        Assert.Equal(1L, await f.H.ScalarAsync($"SELECT count(*) FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}' AND PhaseCode NOT IN (6, 7, 9);"));

        Result<LongRunningOperationDto> replaced = await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None);

        Assert.True(replaced.IsSuccess, replaced.IsFailure ? replaced.Error.Code : null);

        await AssertTheSweepWasReplacedAsync(f, f.Operation, LongRestParkCodes.Cancelled, replaced.Value);

        Assert.Equal(receipts, await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"));

        Assert.Equal(effects, await EffectRowsAsync(f));
    }

    // The suppressed source of an earlier consolidation is erased. It is no leaf, so no trigger invalidates the generation that froze its
    // survivor, and the receipt it was erased with is the footing the survivor's frozen flag rested on: the frozen facts can no longer be
    // proved, whatever the fence says.
    [Fact]
    public async Task A_protected_park_whose_frozen_source_can_no_longer_be_proved_is_retired_by_the_next_authorized_turn()
    {
        await using LongRestMutationMatrixTests.Scenario s = await LongRestMutationMatrixTests.Scenario.CreateAsync(
            isProtected: true, LongRestMutationMatrixTests.Leaves, prepare: static scenario => scenario.ConsolidateAsync());

        MaintenanceFixture f = s.F;

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, await s.ParkAsync());

        await f.RebootAsync();

        await LongRestMutationMatrixTests.ErasureKernelAsync(s, await s.ConsolidatedSourceOfAsync(s.Bystander), reopenScope: true);

        // Nothing but the frozen facts moved: the generation is still there, it is the recipe in force, and its fence resumes.
        LongRestDiscoveryGeneration stored = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, stored.Recipe);

        Assert.True(LongRestFenceCurrency.CanResume(stored.Fence,
            LongRestSweepHarness.Require(await f.Facts.CaptureAsync(f.Scope, f.Epoch.Capture().Stamp, null, CancellationToken.None))));

        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        f.InvokeUnder(turn);

        long receipts = (long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))!;

        EffectRow[] effects = await EffectRowsAsync(f);

        await AssertTheNextAuthorizedTurnRetiresTheParkAsync(f, turn, receipts, effects, scriptTheProvider: false);
    }

    [Fact]
    public async Task A_protected_park_whose_generation_can_still_resume_in_a_later_boot_is_handed_back_to_the_authorized_turn()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedAcrossABootAsync(f);

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        // The recipe, the fence and the frozen sources are all as they were, so the turn that carries authority readmits the park it is handed.
        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, f.Authority, CancellationToken.None)).Id);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);

        f.Handler.Status = HttpStatusCode.OK;

        SummaryOrDecision(f);

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(await f.Maintenance.CreateCapabilityAsync(
            f.Operation, f.Invocation, f.Claim, f.Scope, f.Authority, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed,
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None)).Phase);
    }

    private static async Task<string> GenerationSnapshotAsync(MaintenanceFixture f) =>
        (string)(await f.H.ScalarAsync("SELECT json_group_array(json_array(GenerationId, OperationId, PhaseCode, Revision)) FROM long_rest_discovery_generations;"))!;

    private static async Task<Result<LongRunningOperation>> AdmitWithProofAsync(MaintenanceFixture f, Func<CancellationToken, ValueTask<Result>> proveAuthority)
    {
        FixedCovenantConnectionSource connections = new(f.H.Connection);

        LongRestLiveFenceReader facts = new(f.H.Saga.Context, f.Runtime, f.RecordedAdmission, f.Epoch,
            new CampaignAvailabilityReader(connections), new CampaignPathIdentityReader(connections, null!));

        LongRestSweepFence fence = LongRestSweepHarness.Require(await facts.CaptureAsync(f.Scope, f.Epoch.Capture().Stamp, null, CancellationToken.None));

        return await new LongRestSweepStore(f.H.Saga.Context, f.Clock).AdmitRequestAsync(f.Partition, fence, facts, f.Ownership,
            ((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, CancellationToken.None, proveAuthority);
    }

    [Fact]
    public async Task An_authority_that_lapses_after_the_first_proof_rolls_the_whole_retirement_back_and_frees_the_claim()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedWithEvidenceAsync(f, ErrorCodes.LongRest.StaleFence);

        string before = await ParkedSnapshotAsync(f);

        string generationsBefore = await GenerationSnapshotAsync(f);

        // The authority is live when the park is about to be retired, and gone by the time the transaction would commit. The persisted rows,
        // not total_changes(), prove the rollback: that counter also counts the statements the rollback undid.
        int proofs = 0;

        Result<LongRunningOperation> lapsed = await AdmitWithProofAsync(f, _ => ValueTask.FromResult(++proofs == 1
            ? Result.Success()
            : Result.Failure(new Error(ErrorCodes.Covenant.ForbiddenAuthority, "The turn authority is no longer live."))));

        Assert.True(lapsed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, lapsed.Error.Code);

        // Proved before the first write and again before the commit, and nothing in between survived.
        Assert.Equal(2, proofs);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(generationsBefore, await GenerationSnapshotAsync(f));

        Assert.False(f.Ownership.IsClaimed(f.Operation));

        // The same call with an authority that stays live proves twice as well, and retires the park and starts the new generation.
        proofs = 0;

        Result<LongRunningOperation> live = await AdmitWithProofAsync(f, _ =>
        {
            proofs++;

            return ValueTask.FromResult(Result.Success());
        });

        Assert.True(live.IsSuccess, live.IsFailure ? live.Error.Code : null);

        Assert.Equal(2, proofs);

        Assert.NotEqual(f.Operation, live.Value.Id);

        Assert.Equal(LongRunningOperationState.Abandoned, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.False(f.Ownership.IsClaimed(f.Operation));
    }
}
