using Microsoft.Data.Sqlite;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

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

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.LongRest;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

[Collection("Grimoire")]
public sealed class LongRestTurnPreparationProtectedTests
{
    [Theory]
    [InlineData(ErrorCodes.LongRest.ProviderUnreachable, LongRestSweepPhase.Deferred, "LongRest.ProviderUnreachable", "transient")]
    [InlineData(LongRestParkCodes.AuthorizedRequestRequired, LongRestSweepPhase.Deferred, "long_rest.authorized_request_required", "operation retry alone")]
    [InlineData(ErrorCodes.LongRest.StaleInput, LongRestSweepPhase.Deferred, "LongRest.StaleInput", "new generation")]
    [InlineData(ErrorCodes.LongRest.TransportUnknown, LongRestSweepPhase.Deferred, "LongRest.TransportUnknown", "stored operation outcome")]
    [InlineData("private-source-canary", LongRestSweepPhase.Deferred, "Unknown", "stored operation outcome")]
    [InlineData("LongRest.ProviderUnreachable private-source-canary", LongRestSweepPhase.Deferred, "Unknown", "stored operation outcome")]
    [InlineData(null, LongRestSweepPhase.Publish, "Incomplete", "stored operation outcome")]
    [InlineData(null, LongRestSweepPhase.Completed, null, null)]
    public async Task Protected_deferral_reports_the_actual_operation_closed_reason_and_authority_safe_action(
        string? reason, LongRestSweepPhase phase, string? expectedReason, string? expectedAction)
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Decorated coordinator = new(f.Coordinator)
        {
            ResumeProgress = () => new(f.Operation, Guid.NewGuid(), LongRestSweepScopeKind.Campaign,
                f.CampaignId, phase, 1, "private-source-canary", reason),
        };

        var (preparer, _, _, session) = await BuildAsync(f, coordinator);

        var result = await preparer.PrepareLiveAsync(
            new("private-prompt-canary", Model: "model", SessionId: session, CampaignId: f.CampaignId,
                SkipSpellRouting: true, DisableAllTools: true), f.Invocation, false, CancellationToken.None);

        Assert.Equal(1, coordinator.Resumes);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));

        if (expectedReason is null)
        {
            Assert.True(result.IsSuccess, result.Error.Message);

            Assert.NotNull(result.Value);

            await result.Value.DisposeAsync();
        }
        else
        {
            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.LongRest.Unavailable, result.Error.Code);

            Assert.Contains(f.Operation.ToString("D"), result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains(expectedReason, result.Error.Message, StringComparison.Ordinal);

            Assert.Contains("arcanum operation show " + f.Operation.ToString("D"), result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains(expectedAction!, result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("fresh eligible Session request", result.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("private-", result.Error.Message, StringComparison.Ordinal);

            Assert.DoesNotContain("automatically", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain("cooldown", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(0L, await PendingClaimsAsync(f.H.Connection, session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_protected_debt_finishes_under_the_new_pending_request_before_real_assistant_begin(bool campaign)
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            ownWorker: false, campaign: campaign);

        // Establish actual interrupted protected debt. Recovery can park it but cannot acquire authority.
        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal("long_rest.authorized_request_required", parked.DeferredReason);

        Assert.Equal(0, f.Handler.SendCount);

        (CampaignRollupTurnPreparer preparer, GrimoireRepository repository, CampaignRollupTurnSnapshotTests.ProbeStore rollups, Guid session) =
            await BuildAsync(f, f.Coordinator);

        f.Handler.InsideSend = async () =>
        {
            Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));

            Assert.Equal(1L, await PendingClaimsAsync(f.H.Connection, session));

            Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM external_disclosure_receipts;"))! > 0);

            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";
        };

        PingRequest request = new("Continue after protected maintenance.", Model: "model", SessionId: session,
            CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            request, f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.NotNull(result.Value);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.NotNull(snapshot.ClaimLease);

        Assert.Equal(session, snapshot.ClaimLease!.Claim.SessionId);

        Assert.Equal(SessionTurnClaimState.PendingMaintenance, snapshot.ClaimLease.Claim.State);

        Assert.Equal(f.Scope, snapshot.Campaign);

        Assert.NotNull(snapshot.Authority);

        Assert.Null(snapshot.Artifact);

        Assert.Equal(0, rollups.Reads);

        Assert.True(f.Handler.SendCount > 0);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));

        Result<AssistantReplyBeginReceipt> begun = await repository.BeginClaimedAssistantReplyAsync(
            snapshot.ClaimLease, f.Scope, request.Prompt, "model", CancellationToken.None);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        snapshot.MarkBegun();

        Assert.Equal(snapshot.ClaimLease.FutureAssistantEntryId, begun.Value.AssistantEntryId);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM Entries;"));
    }

    [Fact]
    public async Task A_busy_clean_scope_is_skipped_but_a_busy_protected_partition_stays_fatal_for_the_turn()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Decorated coordinator = new(f.Coordinator) { Clean = () => new Error(ErrorCodes.LongRest.OperationBusy, "Busy."), Partition = () => new Error(ErrorCodes.LongRest.OperationBusy, "Busy.") };

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        // The busy clean scope was skipped (the protected loop was reached); the busy protected partition ended the turn.
        Assert.Equal(1, coordinator.CleanRequests);

        Assert.Equal(1, coordinator.PartitionRequests);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.OperationBusy, result.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);

        // The claim the turn acquired was terminalized, not left pending.
        Assert.Equal(0L, await PendingClaimsAsync(f.H.Connection, session));
    }

    [Theory]
    [InlineData("LongRest.StaleInput")]
    [InlineData("LongRest.StaleFence")]
    [InlineData("LongRest.CurrentnessLost")]
    [InlineData("LongRest.ConfigurationChanged")]
    public async Task A_protected_sweep_parked_by_a_restart_generation_fault_no_longer_fails_the_turn_that_needs_the_partition(string code)
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        // Protected debt, parked; then stamped with the fault that stranded its generation, which nothing can ever readmit.
        Assert.Equal("long_rest.authorized_request_required",
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None)).DeferredReason);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET TerminalErrorCode = $code WHERE Id = $operation;",
            ("$code", code), ("$operation", f.Operation.ToString("N")));

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, f.Coordinator);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue after a stranded protected sweep.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        // The stranded sweep was retired under its own fault and a new generation did the work under this turn's pending claim.
        LongRunningOperation retired = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(code, retired.TerminalErrorCode);

        Assert.Equal(1L, await f.H.ScalarAsync($"SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep' AND State = {(int)LongRunningOperationState.Completed} AND Id <> '{f.Operation:N}';"));

        Assert.True(f.Handler.SendCount > 0);
    }

    [Theory]
    [InlineData("LongRest.ProviderRejected")]
    [InlineData("LongRest.TransportUnknown")]
    public async Task A_protected_sweep_parked_by_an_operator_only_fault_still_ends_the_turn_whatever_authority_the_turn_carries(string code)
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET TerminalErrorCode = $code WHERE Id = $operation;",
            ("$code", code), ("$operation", f.Operation.ToString("N")));

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, f.Coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, result.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(code, parked.TerminalErrorCode);

        Assert.Equal(0L, await PendingClaimsAsync(f.H.Connection, session));
    }

    [Fact]
    public async Task An_unresolved_recipe_ends_a_turn_with_protected_debt_with_the_headline_the_health_row_gives()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        Assert.Equal("long_rest.authorized_request_required",
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None)).DeferredReason);

        // FastModel names a model no provider advertises, so the recipe fails closed after the protected debt was parked.
        ArcanumSettings unresolved = LongRestConfigurationEpochTests.Settings();

        unresolved.FastModel = "not-configured";

        f.Options.Set(unresolved);

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, f.Coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, result.Error.Code);

        // The same sentence the validator warning, the doctor row and the health component give, and nothing the generic refusal said.
        Assert.StartsWith(LongRestRecipeResolver.UnresolvedHeadline, result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(LongRestRecipeResolver.Describe(LongRestRecipeResolver.Problems(unresolved)), result.Error.Message);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await PendingClaimsAsync(f.H.Connection, session));
    }

    [Fact]
    public async Task An_unresolved_recipe_does_not_end_a_turn_that_has_no_protected_debt()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        ArcanumSettings unresolved = LongRestConfigurationEpochTests.Settings();

        unresolved.FastModel = "not-configured";

        f.Options.Set(unresolved);

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, f.Coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        // A refused clean schedule is skipped, so a turn with no protected partition proceeds on the unresolved recipe.
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task A_prepared_snapshot_revalidates_its_campaign_through_the_enricher_the_preparer_was_given()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        CountingEnricher enricher = new();

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, f.Coordinator, enricher);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        // Preparing a turn reads no root of its own: the boundary enriched the Campaign before the preparer was reached.
        Assert.Equal(0, enricher.Calls);

        Result valid = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(valid.IsSuccess, valid.IsFailure ? valid.Error.Message : null);

        Assert.Equal(1, enricher.Calls);
    }

    private sealed class CountingEnricher : ICanonicalCampaignRootEnricher
    {
        internal int Calls;

        public ValueTask<Result<CanonicalCampaignContext>> EnrichAsync(CanonicalCampaignContext resolved, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(resolved));
        }
    }

    [Fact]
    public async Task A_protected_resume_that_is_busy_or_fails_still_ends_the_turn()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Decorated coordinator = new(f.Coordinator) { Resume = () => new Error(ErrorCodes.LongRest.OperationBusy, "Busy.") };

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.Equal(1, coordinator.Resumes);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.OperationBusy, result.Error.Code);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task The_progress_signal_completes_before_the_first_protected_resume_when_a_protected_partition_has_debt()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        CampaignPreparationProgress progress = new();

        bool signalledBeforeResume = false;

        Decorated coordinator = new(f.Coordinator)
        {
            Resume = () =>
            {
                signalledBeforeResume = progress.ProtectedMaintenanceStarted.IsCompleted;

                return new Error(ErrorCodes.LongRest.Unavailable, "Stop here.");
            },
        };

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None, progress: progress);

        Assert.True(result.IsFailure);

        Assert.Equal(1, coordinator.PartitionRequests);

        Assert.Equal(1, coordinator.Resumes);

        Assert.True(signalledBeforeResume);

        Assert.True(progress.ProtectedMaintenanceStarted.IsCompletedSuccessfully);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task A_held_progress_signal_makes_the_first_protected_resume_wait_for_the_acknowledgement()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        CampaignPreparationProgress progress = new(holdUntilAcknowledged: true);

        Decorated coordinator = new(f.Coordinator) { Resume = () => new Error(ErrorCodes.LongRest.Unavailable, "Stop here.") };

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Task<Result<CampaignRollupTurnSnapshot?>> preparing = preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None, progress: progress);

        await progress.ProtectedMaintenanceStarted.WaitAsync(TimeSpan.FromSeconds(10));

        // Told, but held: the protected work has not been resumed while the caller has yet to acknowledge.
        await Task.Delay(200);

        Assert.False(preparing.IsCompleted);

        Assert.Equal(0, coordinator.Resumes);

        progress.AcknowledgeProtectedMaintenance();

        Result<CampaignRollupTurnSnapshot?> result = await preparing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsFailure);

        Assert.Equal(1, coordinator.Resumes);
    }

    [Fact]
    public async Task The_progress_signal_never_completes_for_a_turn_with_no_protected_partition()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        CampaignPreparationProgress progress = new();

        Decorated coordinator = new(f.Coordinator);

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None, progress: progress);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.Equal(0, coordinator.PartitionRequests);

        Assert.False(progress.ProtectedMaintenanceStarted.IsCompleted);
    }

    // An unresolved recipe (here a FastModel that matches no provider) cannot run Long Rest. A clean-only turn is unaffected, because clean
    // work belongs to the worker and its refusal is skipped. A turn that must finish a protected partition first still ends, because a
    // protected failure is fatal: only exact protected work finishing under the pending claim lets maintenance end. The reporting says so.
    [Fact]
    public async Task An_unresolved_recipe_ends_a_turn_that_has_a_protected_partition_before_any_send()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        f.Options.Set(f.Options.CurrentValue with { FastModel = "no-such-fast-model" });

        Decorated coordinator = new(f.Coordinator);

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.Unavailable, result.Error.Code);

        Assert.Equal(1, coordinator.PartitionRequests);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Equal(0L, await PendingClaimsAsync(f.H.Connection, session));
    }

    [Fact]
    public async Task An_unresolved_recipe_does_not_end_a_turn_that_has_only_clean_scope()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        f.Options.Set(f.Options.CurrentValue with { FastModel = "no-such-fast-model" });

        Decorated coordinator = new(f.Coordinator);

        (CampaignRollupTurnPreparer preparer, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid session) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: session, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.Equal(1, coordinator.CleanRequests);

        Assert.Equal(0, coordinator.PartitionRequests);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task The_progress_signal_never_completes_when_the_protected_partition_is_already_completed()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        // The first turn finishes the protected debt under its pending claim.
        (CampaignRollupTurnPreparer first, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid firstSession) = await BuildAsync(f, f.Coordinator);

        Result<CampaignRollupTurnSnapshot?> finished = await first.PrepareLiveAsync(
            new("Finish.", Model: "model", SessionId: firstSession, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None);

        Assert.True(finished.IsSuccess, finished.IsFailure ? finished.Error.Code : null);

        await finished.Value!.DisposeAsync();

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        int sends = f.Handler.SendCount;

        Assert.True(sends > 0);

        // The next turn still has a protected partition with sources, but its sweep is already completed: there is no maintenance
        // to announce, so the caller is not told and nothing is sent.
        CampaignPreparationProgress progress = new();

        Decorated coordinator = new(f.Coordinator);

        (CampaignRollupTurnPreparer second, GrimoireRepository _, CampaignRollupTurnSnapshotTests.ProbeStore _, Guid secondSession) = await BuildAsync(f, coordinator);

        Result<CampaignRollupTurnSnapshot?> result = await second.PrepareLiveAsync(
            new("Continue.", Model: "model", SessionId: secondSession, CampaignId: f.CampaignId, SkipSpellRouting: true, DisableAllTools: true),
            f.Invocation, false, CancellationToken.None, progress: progress);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);

        await using CampaignRollupTurnSnapshot snapshot = result.Value!;

        Assert.Equal(1, coordinator.PartitionRequests);

        Assert.Equal(0, coordinator.Resumes);

        Assert.Equal(sends, f.Handler.SendCount);

        Assert.False(progress.ProtectedMaintenanceStarted.IsCompleted);
    }

    /// <summary>Delegates to the real coordinator unless a member is scripted to answer instead.</summary>
    private sealed class Decorated(ILongRestSweepCoordinator inner) : ILongRestSweepCoordinator
    {
        internal Func<Error?>? Clean { get; init; }

        internal Func<Error?>? Partition { get; init; }

        internal Func<Error?>? Resume { get; init; }

        internal Func<LongRestSweepProgress>? ResumeProgress { get; init; }

        internal int CleanRequests;

        internal int PartitionRequests;

        internal int Resumes;

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
        {
            CleanRequests++;

            return Clean?.Invoke() is { } error ? Task.FromResult(Result<LongRunningOperationDto>.Failure(error)) : inner.RequestAsync(scope, ct);
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct)
        {
            PartitionRequests++;

            return Partition?.Invoke() is { } error ? Task.FromResult(Result<LongRunningOperationDto>.Failure(error)) : inner.RequestAsync(scope, partition, ct);
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct)
        {
            PartitionRequests++;

            return Partition?.Invoke() is { } error ? Task.FromResult(Result<LongRunningOperationDto>.Failure(error)) : inner.RequestAsync(scope, partition, authority, ct);
        }

        public Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operationId, LongRestMaintenanceCapability? capability, CancellationToken ct)
        {
            Resumes++;

            if (ResumeProgress is not null) return Task.FromResult(Result<LongRestSweepProgress>.Success(ResumeProgress()));

            return Resume?.Invoke() is { } error ? Task.FromResult(Result<LongRestSweepProgress>.Failure(error)) : inner.ResumeAsync(operationId, capability, ct);
        }
    }

    private static async Task<(CampaignRollupTurnPreparer Preparer, GrimoireRepository Repository, CampaignRollupTurnSnapshotTests.ProbeStore Rollups, Guid Session)>
        BuildAsync(LongRestMaintenanceTests.MaintenanceFixture f, ILongRestSweepCoordinator coordinator, ICanonicalCampaignRootEnricher? enricher = null)
    {
        Guid session = await f.H.SessionAsync(f.CampaignId);

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(f.H.Connection), new CovenantQuotaGuard(), f.Epoch.Capture().Stamp.BootId);

        GrimoireRepository repository = new(f.H.Saga.Context, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(f.Options.CurrentValue), null, null,
            FixtureOrdinaryConnectionFactory.For(f.H.Saga.Context), FixtureLabeledArtifactGuard.For(f.H.Saga.Context), memoryDelegation: null);

        CampaignRollupTurnSnapshotTests.ProbeStore rollups = new();

        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<ArcanumSettings>>(f.Options)
            .AddSingleton<ICampaignRollupStore>(rollups)
            .AddSingleton<ICanonicalCampaignContextResolver>(new ExactScopeResolver(f.Scope))
            .AddSingleton(enricher ?? new PassThroughRootEnricher())
            .AddSingleton<ICovenantOperationGate>(f.Gate)
            .AddSingleton<ICampaignRollupMaintenance>(new CampaignRollupTurnSnapshotTests.RefusingMaintenance())
            .AddSingleton<ISessionTurnClaimBeginStore>(repository)
            .AddSingleton<ISessionTurnBeginStore>(repository)
            .AddSingleton<ISessionTurnClaimCoordinator>(claims)
            .AddSingleton<ISessionTurnClaimLookup>(claims)
            .AddSingleton<ICovenantAuthoritySnapshotProvider>(new CovenantAuthoritySnapshotProvider(f.Runtime))
            .AddSingleton(coordinator)
            .AddSingleton<ILongRestMaintenance>(f.Maintenance)
            .AddSingleton<ILongRestConfigurationProvider>(f.Epoch)
            .AddSingleton<ILongRestSweepStore>(new LongRestSweepStore(f.H.Saga.Context, f.Clock))
            .AddSingleton<ILongRestDiscoveryStore>(f.H.Store)
            .AddSingleton<ILongRunningOperationStore>(f.Operations)
            .BuildServiceProvider();

        // Current constructors consume only current ports. No proposed constructor/type is referenced.
        return (ActivatorUtilities.CreateInstance<CampaignRollupTurnPreparer>(services), repository, rollups, session);
    }

    private static async Task<long> PendingClaimsAsync(SqliteConnection connection, Guid session)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM session_turn_claims WHERE SessionId = $session AND StateCode = 1;";

        command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ExactScopeResolver(CanonicalCampaignContext scope) : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(scope));
    }
}
