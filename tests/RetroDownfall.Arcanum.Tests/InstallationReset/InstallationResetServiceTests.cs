using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetServiceTests
{

    [Fact]
    public async Task BindOnlineDataPlan_rebinds_only_the_Covenant_aware_data_identity()
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        InstallationResetService service = new(
            new FakeDataService(localDataPlan),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup(),
            stateRoots: new FixedStateRoots(["/state"]));

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        DataRetentionPlan onlinePlan = localDataPlan with
        {
            PlanId = "online-covenant-plan",
            GeneratedAt = localDataPlan.GeneratedAt.AddMinutes(1),
            Covenant = new DataRetentionCovenantInventory(
                Rows: 11,
                ManagedFiles: 2,
                LocalArtifacts: 3,
                AffectedSessions: 4,
                PossibleDisclosures: 5,
                Core.Covenant.CovenantDisclosureCountKind.Exact),
        };

        Result<InstallationResetPlan> result = service.BindOnlineDataPlan(
            request,
            localPlan,
            onlinePlan);

        Assert.True(result.IsSuccess, result.Error.Message);

        InstallationResetPlan rebound = result.Value;

        Assert.NotEqual(localPlan.PlanId, rebound.PlanId);

        Assert.NotEqual(
            localPlan.AcceptedBinding.BindingId,
            rebound.AcceptedBinding.BindingId);

        Assert.Equal([onlinePlan.PlanId], rebound.AcceptedBinding.DataPlanIds);

        InstallationResetTargetDescriptor database = Assert.Single(
            rebound.Targets,
            static target => target.Role is InstallationResetTargetRole.Database);

        Assert.Equal(
            "canonical-data-plan:" + onlinePlan.PlanId,
            database.DatabasePredicate);

        Assert.Equal(localPlan.Scope, rebound.Scope);

        Assert.Equal(localPlan.Workspace, rebound.Workspace);

        Assert.Equal(localPlan.GeneratedAt, rebound.GeneratedAt);

        Assert.Equal(localPlan.AcceptedBinding.SelectedRoots, rebound.AcceptedBinding.SelectedRoots);

        Assert.Equal(localPlan.AcceptedBinding.ExcludedRoots, rebound.AcceptedBinding.ExcludedRoots);

        Assert.Equal(localPlan.PreservedBackups, rebound.PreservedBackups);

        Assert.Equal(localPlan.Credentials, rebound.Credentials);

        Assert.Equal(localPlan.Exclusions, rebound.Exclusions);

        Assert.Equal(localPlan.Blockers, rebound.Blockers);

        Assert.Equal(localPlan.Rows, rebound.Rows);

        Assert.Equal(localPlan.Files, rebound.Files);

        Assert.Equal(localPlan.EstimatedBytes, rebound.EstimatedBytes);

        Assert.Contains(
            rebound.Targets,
            static target => target.Role is InstallationResetTargetRole.Daemon
                && target.ResourceId == "platform-daemon-registration");

    }

    [Theory]
    [InlineData("request")]
    [InlineData("items")]
    [InlineData("blockers")]
    [InlineData("conflicts")]
    [InlineData("rows")]
    [InlineData("files")]
    [InlineData("estimated-bytes")]
    [InlineData("derived-records")]
    [InlineData("candidate-ids")]
    [InlineData("requires-confirmation")]
    public async Task BindOnlineDataPlan_rejects_each_changed_ordinary_candidate_dimension(
        string dimension)
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        InstallationResetService service = new(
            new FakeDataService(localDataPlan),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        DataRetentionPlan changed = MutateOrdinaryPlan(
            localDataPlan with
            {
                PlanId = "online-covenant-plan",
                Covenant = new DataRetentionCovenantInventory(
                    Rows: 11,
                    ManagedFiles: 2,
                    LocalArtifacts: 3,
                    AffectedSessions: 4,
                    PossibleDisclosures: 5,
                    Core.Covenant.CovenantDisclosureCountKind.Exact),
            },
            dimension);

        Result<InstallationResetPlan> result = service.BindOnlineDataPlan(
            request,
            localPlan,
            changed);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task BindOnlineDataPlan_rejects_a_missing_online_data_identity(
        string onlinePlanId)
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        InstallationResetService service = new(
            new FakeDataService(localDataPlan),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetPlan> result = service.BindOnlineDataPlan(
            request,
            localPlan,
            localDataPlan with { PlanId = onlinePlanId });

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

    }

    [Fact]
    public async Task PrepareOnlineDataHandoff_revalidates_the_rebound_plan_before_publication()
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        FakeActiveStore active = new();

        InstallationResetService service = new(
            new FakeDataService(
                localDataPlan,
                localDataPlan with { CandidateIds = ["changed"] }),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        InstallationResetPlan rebound = service.BindOnlineDataPlan(
            planRequest,
            localPlan,
            localDataPlan with { PlanId = "online-data-plan" }).Value;

        Result<InstallationResetOnlineDataHandoff> result = await service.PrepareAsync(
            new InstallationResetApplyRequest(planRequest, rebound.PlanId),
            rebound,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.False(active.Written);

    }

    [Fact]
    public async Task PrepareOnlineDataHandoff_publishes_one_idempotent_Prepared_owner()
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        FakeActiveStore active = new();

        InstallationResetService service = new(
            new FakeDataService(localDataPlan),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        InstallationResetPlan rebound = service.BindOnlineDataPlan(
            planRequest,
            localPlan,
            localDataPlan with { PlanId = "online-data-plan" }).Value;

        InstallationResetApplyRequest applyRequest = new(planRequest, rebound.PlanId);

        Result<InstallationResetOnlineDataHandoff> prepared = await service.PrepareAsync(
            applyRequest,
            rebound,
            CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.NotEqual(Guid.Empty, prepared.Value.RequestedOperationId);

        Assert.Equal(rebound.PlanId, prepared.Value.InstallationPlanId);

        Assert.Equal("online-data-plan", prepared.Value.DataPlanId);

        Assert.False(prepared.Value.DataResetCompleted);

        InstallationResetActiveRecord record = Assert.IsType<InstallationResetActiveRecord>(
            active.Record);

        Assert.Equal(prepared.Value.RequestedOperationId, record.OperationId);

        Assert.Equal(InstallationResetPhase.Prepared, record.Phase);

        Assert.Equal(InstallationResetDataHandoff.HostFactoryErasure, record.DataHandoff);

        Assert.Null(record.OnlineDataCompletion);

        Result<InstallationResetOnlineDataHandoff?> replay = await service.ReadAsync(
            applyRequest,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(prepared.Value, replay.Value);

        Assert.Single(active.Writes);

    }

    [Fact]
    public async Task PrepareOnlineDataHandoff_rejects_workspace_and_later_phase_records()
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        FakeActiveStore active = new();

        InstallationResetService service = new(
            new FakeDataService(localDataPlan),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        InstallationResetPlan rebound = service.BindOnlineDataPlan(
            planRequest,
            localPlan,
            localDataPlan with { PlanId = "online-data-plan" }).Value;

        InstallationResetActiveRecord later = CreateActive(
            rebound,
            InstallationResetPhase.DataResetComplete) with
        {
            DataHandoff = InstallationResetDataHandoff.HostFactoryErasure,
        };

        active.Seed(later);

        Result<InstallationResetOnlineDataHandoff?> result = await service.ReadAsync(
            new InstallationResetApplyRequest(planRequest, rebound.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, result.Error.Code);

        InstallationResetPlanRequest workspaceRequest = new(
            InstallationResetScope.Workspace,
            "/invocation");

        Result<InstallationResetOnlineDataHandoff> workspace = await service.PrepareAsync(
            new InstallationResetApplyRequest(workspaceRequest, rebound.PlanId),
            rebound with { Scope = InstallationResetScope.Workspace },
            CancellationToken.None);

        Assert.True(workspace.IsFailure);

    }

    [Theory]
    [InlineData("unreconciled")]
    [InlineData("data-plan")]
    [InlineData("requested-operation")]
    [InlineData("missing-requested-operation")]
    [InlineData("server-operation")]
    [InlineData("negative-count")]
    public async Task RecordCompleted_rejects_every_untrusted_host_completion_dimension(
        string dimension)
    {

        PreparedOnlineHandoffFixture fixture = await PrepareOnlineHandoffAsync();

        DataRetentionApplyResult completion = CreateOnlineDataCompletion(
            fixture.Handoff);

        completion = dimension switch
        {
            "unreconciled" => completion with { Reconciled = false },
            "data-plan" => completion with { PlanId = "different-data-plan" },
            "requested-operation" => completion with
            {
                RequestedOperationId = Guid.Parse(
                    "41414141-4141-4141-8141-414141414141"),
            },
            "missing-requested-operation" => completion with
            {
                RequestedOperationId = null,
            },
            "server-operation" => completion with
            {
                OperationId = fixture.Handoff.RequestedOperationId,
            },
            "negative-count" => completion with { RowsDeleted = -1 },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };

        Result recorded = await fixture.Service.RecordCompletedAsync(
            fixture.Handoff,
            completion,
            CancellationToken.None);

        Assert.True(recorded.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, recorded.Error.Code);

        Assert.Null(fixture.Active.Record!.OnlineDataCompletion);

        Assert.Single(fixture.Active.Writes);

        Assert.False(fixture.Active.Retired);

    }

    [Fact]
    public async Task RecordCompleted_durably_appends_one_monotonic_content_free_proof()
    {

        PreparedOnlineHandoffFixture fixture = await PrepareOnlineHandoffAsync();

        DataRetentionApplyResult completion = CreateOnlineDataCompletion(
            fixture.Handoff);

        Result first = await fixture.Service.RecordCompletedAsync(
            fixture.Handoff,
            completion,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        InstallationResetActiveRecord proven = Assert.IsType<InstallationResetActiveRecord>(
            fixture.Active.Record);

        Assert.Equal(InstallationResetPhase.Prepared, proven.Phase);

        Assert.False(proven.PointOfNoReturn);

        InstallationResetOnlineDataCompletion proof =
            Assert.IsType<InstallationResetOnlineDataCompletion>(
                proven.OnlineDataCompletion);

        Assert.Equal(completion.OperationId, proof.ServerOperationId);

        Assert.Equal(fixture.Handoff.RequestedOperationId, proof.RequestedOperationId);

        Assert.Equal(fixture.Handoff.DataPlanId, proof.DataPlanId);

        Assert.Equal(7, proof.RowsDeleted);

        Assert.Equal(3, proof.FilesDeleted);

        Assert.Equal(19, proof.EstimatedBytesDeleted);

        Assert.Equal(2, proof.DerivedRecordsDeleted);

        Result replay = await fixture.Service.RecordCompletedAsync(
            fixture.Handoff,
            completion,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(2, fixture.Active.Writes.Count);

        Result<InstallationResetOnlineDataHandoff?> read = await fixture.Service.ReadAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.True(read.Value!.DataResetCompleted);

        Result changed = await fixture.Service.RecordCompletedAsync(
            fixture.Handoff,
            completion with
            {
                OperationId = Guid.Parse("42424242-4242-4242-8242-424242424242"),
            },
            CancellationToken.None);

        Assert.True(changed.IsFailure);

        Assert.Equivalent(proof, fixture.Active.Record!.OnlineDataCompletion, strict: true);

        Assert.False(fixture.Active.Retired);

    }

    [Fact]
    public async Task RetirePreEffect_retires_only_the_exact_unproven_Prepared_handoff()
    {

        PreparedOnlineHandoffFixture fixture = await PrepareOnlineHandoffAsync();

        Result mismatch = await fixture.Service.RetirePreEffectAsync(
            fixture.Handoff with
            {
                RequestedOperationId = Guid.Parse(
                    "43434343-4343-4343-8343-434343434343"),
            },
            CancellationToken.None);

        Assert.True(mismatch.IsFailure);

        Assert.False(fixture.Active.Retired);

        Assert.NotNull(fixture.Active.Record);

        Result retired = await fixture.Service.RetirePreEffectAsync(
            fixture.Handoff,
            CancellationToken.None);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        Assert.True(fixture.Active.Retired);

        Assert.Equal(
            fixture.Handoff.RequestedOperationId,
            Assert.Single(fixture.Active.RetiredOperationIds));

    }

    [Fact]
    public async Task RetirePreEffect_preserves_a_durable_completion_proof()
    {

        PreparedOnlineHandoffFixture fixture = await PrepareOnlineHandoffAsync();

        Assert.True((await fixture.Service.RecordCompletedAsync(
            fixture.Handoff,
            CreateOnlineDataCompletion(fixture.Handoff),
            CancellationToken.None)).IsSuccess);

        Result retired = await fixture.Service.RetirePreEffectAsync(
            fixture.Handoff,
            CancellationToken.None);

        Assert.True(retired.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, retired.Error.Code);

        Assert.False(fixture.Active.Retired);

        Assert.NotNull(fixture.Active.Record!.OnlineDataCompletion);

    }

    [Fact]
    public async Task Prepared_online_handoff_without_durable_proof_never_mutates_offline()
    {

        PreparedOnlineHandoffFixture fixture = await PrepareOnlineHandoffAsync();

        Result<InstallationResetResult> result = await fixture.Service.ApplyAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ResumeRequired);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Value.ErrorCode);

        Assert.False(fixture.PreDataMutation.Executed);

        Assert.Empty(fixture.Data.ApplyRequests);

        Assert.False(fixture.OfflineCleanup.Executed);

        Assert.Empty(fixture.Credentials.DeleteRequests);

        Assert.False(fixture.Active.Retired);

        Assert.Equal(InstallationResetPhase.Prepared, fixture.Active.Record!.Phase);

    }

    [Fact]
    public async Task Prepared_online_handoff_All_uses_proof_when_the_Campaign_catalog_is_gone()
    {

        string workspaceRoot = Path.GetFullPath(Path.Combine(
            "/tmp",
            "online-handoff-all-workspace"));

        FakeWorkspaceResolver resolver = new(
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), workspaceRoot));

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        FakeDataService data = new(localDataPlan);

        FakeActiveStore active = new();

        FakePreDataMutation preDataMutation = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: resolver,
            preDataMutation: preDataMutation);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            Path.Combine(workspaceRoot, "src"));

        InstallationResetPlan localPlan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        InstallationResetPlan rebound = service.BindOnlineDataPlan(
            planRequest,
            localPlan,
            localDataPlan with { PlanId = "online-data-plan" }).Value;

        InstallationResetApplyRequest request = new(planRequest, rebound.PlanId);

        InstallationResetOnlineDataHandoff handoff = (await service.PrepareAsync(
            request,
            rebound,
            CancellationToken.None)).Value;

        Assert.True((await service.RecordCompletedAsync(
            handoff,
            CreateOnlineDataCompletion(handoff),
            CancellationToken.None)).IsSuccess);

        resolver.Failure = new Error(
            ErrorCodes.Data.InventoryUnavailable,
            "The Campaign catalog was removed by the authenticated host reset.");

        Result<InstallationResetResult> result = await service.ApplyAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(InstallationResetPhase.Completed, result.Value.Phase);

        Assert.True(preDataMutation.Executed);

        Assert.Empty(data.ApplyRequests);

        Assert.True(active.Retired);

    }

    [Fact]
    public async Task Prepared_online_handoff_All_can_replay_when_the_Campaign_catalog_is_gone()
    {

        string workspaceRoot = Path.GetFullPath(Path.Combine(
            "/tmp",
            "online-handoff-all-replay-workspace"));

        FakeWorkspaceResolver resolver = new(
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), workspaceRoot));

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        FakeDataService data = new(localDataPlan);

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: resolver);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            Path.Combine(workspaceRoot, "src"));

        InstallationResetPlan localPlan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        InstallationResetPlan rebound = service.BindOnlineDataPlan(
            planRequest,
            localPlan,
            localDataPlan with { PlanId = "online-data-plan" }).Value;

        InstallationResetApplyRequest request = new(planRequest, rebound.PlanId);

        InstallationResetOnlineDataHandoff prepared = (await service.PrepareAsync(
            request,
            rebound,
            CancellationToken.None)).Value;

        resolver.Failure = new Error(
            ErrorCodes.Data.InventoryUnavailable,
            "The Campaign catalog may already have been removed by the host reset.");

        Result<InstallationResetOnlineDataHandoff?> replay = await service.ReadAsync(
            request,
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(prepared, replay.Value);

        Assert.False(replay.Value!.DataResetCompleted);

        Assert.Empty(data.ApplyRequests);

        Assert.False(active.Retired);

    }

    [Fact]
    public async Task Prepared_online_handoff_with_durable_proof_runs_daemon_and_skips_offline_factory()
    {

        PreparedOnlineHandoffFixture fixture = await PrepareOnlineHandoffAsync(
            [
                new InstallationResetCredentialSummary(
                    "accepted-account",
                    InstallationResetItemStatus.Pending),
            ]);

        DataRetentionApplyResult completion = CreateOnlineDataCompletion(
            fixture.Handoff);

        Assert.True((await fixture.Service.RecordCompletedAsync(
            fixture.Handoff,
            completion,
            CancellationToken.None)).IsSuccess);

        Result<InstallationResetResult> result = await fixture.Service.ApplyAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.ResumeRequired);

        Assert.True(result.Value.PointOfNoReturn);

        Assert.Equal(7, result.Value.RowsDeleted);

        Assert.Equal(3, result.Value.FilesDeleted);

        Assert.Equal(19, result.Value.EstimatedBytesDeleted);

        Assert.True(fixture.PreDataMutation.Executed);

        Assert.Empty(fixture.Data.ApplyRequests);

        Assert.True(fixture.OfflineCleanup.Executed);

        Assert.NotEmpty(fixture.Credentials.DeleteRequests);

        Assert.Equal(
            InstallationResetItemStatus.Deleted,
            Assert.Single(result.Value.CredentialResults).Status);

        InstallationResetActiveRecord dataCheckpoint = Assert.Single(
            fixture.Active.Writes,
            static record => record.Phase is InstallationResetPhase.DataResetComplete);

        Assert.True(dataCheckpoint.PointOfNoReturn);

        Assert.Equal(7, dataCheckpoint.RowsDeleted);

        Assert.Equal(3, dataCheckpoint.FilesDeleted);

        Assert.Equal(19, dataCheckpoint.EstimatedBytesDeleted);

        Assert.NotNull(dataCheckpoint.OnlineDataCompletion);

        Assert.True(fixture.Active.Retired);

    }

    [Fact]
    public async Task Workspace_apply_preserves_global_daemon_registration()
    {

        DataRetentionWorkspaceBinding workspace = new(
            Guid.Parse("50505050-5050-5050-5050-505050505050"),
            "/workspace");

        FakePreDataMutation preDataMutation = new();

        FakeDataService data = new(CreateDataPlan("workspace-data"));

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup(),
            workspaceResolver: new FakeWorkspaceResolver(workspace),
            preDataMutation: preDataMutation);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Workspace,
            Path.Combine(workspace.WorkspaceRoot, "src"));

        Result<InstallationResetPlan> planned = await service.PlanAsync(request);

        Assert.True(planned.IsSuccess, planned.Error.Message);

        Result<InstallationResetResult> applied = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, planned.Value.PlanId));

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.False(preDataMutation.Executed);

    }

    [Fact]
    public async Task Dry_run_composes_global_data_and_credentials_without_creating_active_state()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new(
            [
                new InstallationResetCredentialSummary(
                    "master-api-key",
                    InstallationResetItemStatus.Pending),
            ]);

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            credentials,
            active,
            new FakeOfflineCleanup());

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/invocation"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(["global-data"], result.Value.AcceptedBinding.DataPlanIds);

        Assert.Equal(["master-api-key"], result.Value.AcceptedBinding.CredentialAccounts);

        Assert.False(active.Written);

        Assert.Equal(InstallationResetDataScope.Global, Assert.Single(data.PlanRequests).Scope);

    }

    [Fact]
    public async Task Unavailable_credential_inventory_is_reported_as_a_dry_run_blocker()
    {

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory(
            [
                new InstallationResetCredentialSummary(
                    "master-api-key",
                    InstallationResetItemStatus.Unavailable,
                    ErrorCodes.Data.CredentialInventoryUnavailable),
            ]),
            new FakeActiveStore(),
            new FakeOfflineCleanup());

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/invocation"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.CredentialInventoryAvailable);

        InstallationResetIssueSummary blocker = Assert.Single(
            result.Value.Blockers,
            static item => item.Code == ErrorCodes.Data.CredentialInventoryUnavailable);

        Assert.Equal("master-api-key", blocker.ResourceId);

    }

    [Fact]
    public async Task Workspace_plan_resolves_the_most_specific_registered_campaign()
    {

        string parent = Path.GetFullPath(Path.Combine("/tmp", "campaign-parent"));

        string nested = Path.Combine(parent, "nested");

        Guid nestedId = Guid.NewGuid();

        FakeWorkspaceResolver resolver = new(
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), parent),
            new DataRetentionWorkspaceBinding(nestedId, nested));

        FakeDataService data = new(CreateDataPlan("workspace-data"));

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup(),
            workspaceResolver: resolver);

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Workspace,
                Path.Combine(nested, "src")),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(nestedId, result.Value.Workspace!.CampaignId);

        Assert.Equal(nested, result.Value.Workspace.WorkspaceRoot);

        InstallationResetDataPlanRequest dataRequest = Assert.Single(data.PlanRequests);

        Assert.Equal(InstallationResetDataScope.Workspace, dataRequest.Scope);

        Assert.Equal(result.Value.Workspace, dataRequest.Workspace);

        Assert.Empty(result.Value.Credentials);

        Assert.Empty(result.Value.AcceptedBinding.CredentialAccounts);

    }

    [Fact]
    public async Task Workspace_plan_passes_nested_campaign_roots_to_filesystem_inventory()
    {

        string root = Path.GetFullPath(Path.Combine("/tmp", "campaign-parent"));

        string nested = Path.Combine(root, ".arcanum", "nested-campaign");

        FakeWorkspaceResolver resolver = new(
            new InstallationResetWorkspaceResolution(
                new DataRetentionWorkspaceBinding(Guid.NewGuid(), root),
                [nested]));

        FakeOfflineCleanup cleanup = new();

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("workspace-data")),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            cleanup,
            workspaceResolver: resolver);

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Workspace,
                Path.Combine(root, "src")),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal([nested], Assert.Single(cleanup.PlanExcludedRoots));

    }

    [Fact]
    public void Workspace_resolution_returns_nested_campaigns_inside_selected_state_root()
    {

        string root = Path.GetFullPath(Path.Combine("/tmp", "campaign-parent"));

        string nested = Path.Combine(root, ".arcanum", "nested-campaign");

        Result<InstallationResetWorkspaceResolution> result =
            InstallationResetWorkspaceResolver.Resolve(
                Path.Combine(root, "src"),
                [
                    new Campaign { Id = Guid.NewGuid(), Path = root },
                    new Campaign { Id = Guid.NewGuid(), Path = nested },
                ]);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(root, result.Value.Workspace.WorkspaceRoot);

        Assert.Equal([nested], result.Value.ExcludedRoots);

    }

    [Fact]
    public async Task All_resume_uses_resolver_exclusions_for_an_ordinary_nested_campaign()
    {

        string root = Path.GetFullPath(Path.Combine("/tmp", "campaign-parent-resume"));

        string nested = Path.Combine(root, "nested-campaign");

        Result<InstallationResetWorkspaceResolution> resolved =
            InstallationResetWorkspaceResolver.Resolve(
                Path.Combine(root, "src"),
                [
                    new Campaign { Id = Guid.NewGuid(), Path = root },
                    new Campaign { Id = Guid.NewGuid(), Path = nested },
                ]);

        Assert.True(resolved.IsSuccess, resolved.Error.Message);

        FakeActiveStore active = new();

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: new FakeWorkspaceResolver(resolved.Value));

        InstallationResetPlanRequest acceptedRequest = new(
            InstallationResetScope.All,
            Path.Combine(root, "src"));

        InstallationResetPlan plan = (await service.PlanAsync(
            acceptedRequest,
            CancellationToken.None)).Value;

        active.Seed(CreateActive(
            plan,
            InstallationResetPhase.Prepared,
            pointOfNoReturn: true));

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(
                new InstallationResetPlanRequest(
                    InstallationResetScope.All,
                    Path.Combine(nested, "src")),
                plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, result.Error.Code);

    }

    [Fact]
    public async Task Workspace_plan_rejects_a_selected_root_that_overlaps_reset_control()
    {

        string guardedRoot = Path.GetFullPath(Path.Combine("/tmp", "grimoire"));

        string controlParent = Path.GetDirectoryName(
            new InstallationResetActiveStore(guardedRoot).ActivePath)!;

        FakeWorkspaceResolver resolver = new(
            new InstallationResetWorkspaceResolution(
                new DataRetentionWorkspaceBinding(Guid.NewGuid(), controlParent),
                []));

        FakeOfflineCleanup cleanup = new();

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("workspace-data")),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            cleanup,
            workspaceResolver: resolver,
            controlPaths: new InstallationResetControlPaths(guardedRoot));

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Workspace,
                Path.Combine(controlParent, "src")),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.WorkspaceOverlap, result.Error.Code);

        Assert.Empty(cleanup.PlanSelectedRoots);

    }

    [Fact]
    public async Task Plan_binds_the_exact_selected_files_backups_and_exclusions()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeOfflineCleanup cleanup = new()
        {
            Inventory = Result<InstallationResetFileSystemInventory>.Success(
                new InstallationResetFileSystemInventory(
                    [
                        new InstallationResetTargetDescriptor(
                            "installation-file",
                            InstallationResetTargetRole.FileSystem,
                            "/state/arcanum.json",
                            "/state/arcanum.json",
                            DatabasePredicate: null,
                            new InstallationResetFileIdentity("identity", 5, 1),
                            Rows: null,
                            Files: 1,
                            EstimatedBytes: 5),
                    ],
                    [
                        new InstallationResetPreservedBackup(
                            "/state/backup.arcbackup",
                            new InstallationResetFileIdentity("backup", 68, 1)),
                    ],
                    [
                        new InstallationResetExclusion(
                            "nested-campaign",
                            "/state/nested",
                            "excluded"),
                    ],
                    Files: 1,
                    EstimatedBytes: 5)),
        };

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            cleanup,
            stateRoots: new FixedStateRoots(["/state"]));

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/invocation"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Contains(
            result.Value.Targets,
            static target => target.CanonicalPath == "/state/arcanum.json");

        Assert.Contains(
            result.Value.Targets,
            static target => target.Role is InstallationResetTargetRole.Daemon
                && target.ResourceId == "platform-daemon-registration");

        Assert.Equal(["/state"], result.Value.AcceptedBinding.SelectedRoots);

        Assert.Equal(
            "/state/backup.arcbackup",
            Assert.Single(result.Value.AcceptedBinding.PreservedBackups).CanonicalPath);

        Assert.Equal(["/state/nested"], result.Value.AcceptedBinding.ExcludedRoots);

        Assert.Equal(1, result.Value.Files);

        Assert.Equal(5, result.Value.EstimatedBytes);

        Assert.Equal(["/state"], Assert.Single(cleanup.PlanSelectedRoots));

    }

    [Fact]
    public async Task Plan_id_changes_when_an_ordinary_selected_file_identity_changes()
    {

        FakeOfflineCleanup cleanup = new();

        cleanup.Inventory = InventoryWithIdentity("first-identity");

        InstallationResetService service = new(
            new FakeDataService(
                CreateDataPlan("global-data"),
                CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            cleanup,
            stateRoots: new FixedStateRoots(["/state"]));

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        Result<InstallationResetPlan> first = await service.PlanAsync(
            request,
            CancellationToken.None);

        cleanup.Inventory = InventoryWithIdentity("replacement-identity");

        Result<InstallationResetPlan> second = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.True(second.IsSuccess, second.Error.Message);

        Assert.NotEqual(first.Value.PlanId, second.Value.PlanId);

    }

    [Fact]
    public async Task Apply_replans_before_active_publication_and_binds_the_expected_plan()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new([]);

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new();

        InstallationResetService service = new(data, credentials, active, cleanup);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(InstallationResetPhase.Completed, result.Value.Phase);

        Assert.Equal(plan.PlanId, result.Value.PlanId);

        Assert.True(result.Value.Verification.Succeeded);

        Assert.False(result.Value.ResumeRequired);

        Assert.Equal(2, data.PlanRequests.Count);

        Assert.Single(data.ApplyRequests);

        Assert.Equal("global-data", data.ApplyRequests[0].ExpectedPlanId);

        Assert.True(active.Written);

        Assert.True(active.Retired);

        Assert.True(cleanup.Executed);

    }

    [Fact]
    public async Task Apply_rejects_plan_drift_before_active_publication()
    {

        FakeDataService data = new(
            CreateDataPlan("initial"),
            CreateDataPlan("changed"));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.False(active.Written);

        Assert.Empty(data.ApplyRequests);

    }

    [Theory]

    [InlineData(ErrorCodes.Data.RecoveryRequired)]

    [InlineData(ErrorCodes.Data.ReconciliationFailed)]

    public async Task Uncertain_data_failure_keeps_prepared_record_for_resume(
        string dataErrorCode)
    {

        FakeDataService data = new(CreateDataPlan("global-data"))
        {
            ApplyResult = Result<DataRetentionApplyResult>.Failure(new Error(
                dataErrorCode,
                "The canonical reset outcome is uncertain.")),
        };

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ResumeRequired);

        Assert.True(result.Value.PointOfNoReturn);

        Assert.Equal(InstallationResetPhase.Prepared, result.Value.Phase);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Value.ErrorCode);

        Assert.False(active.Retired);

        Assert.NotNull(active.Record);

        Assert.Equal(InstallationResetPhase.Prepared, active.Record.Phase);

        Assert.True(active.Record.PointOfNoReturn);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, active.Record.LastErrorCode);

    }

    [Fact]
    public async Task Post_data_failure_returns_a_resumable_result_and_keeps_active_state()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new()
        {
            Result = Result<InstallationResetOfflineCleanupResult>.Failure(
                new Error(ErrorCodes.Data.FileLocked, "locked")),
        };

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            cleanup);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(InstallationResetPhase.DataResetComplete, result.Value.Phase);

        Assert.True(result.Value.PointOfNoReturn);

        Assert.True(result.Value.ResumeRequired);

        Assert.Equal(ErrorCodes.Data.FileLocked, result.Value.ErrorCode);

        Assert.False(active.Retired);

    }

    [Fact]
    public async Task Daemon_failure_after_active_publication_is_resumable_before_data_deletion()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new();

        FakePreDataMutation preData = new()
        {
            Result = Result.Failure(new Error(
                "Daemon.UninstallFailed",
                "daemon uninstall failed")),
        };

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            preDataMutation: preData);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(InstallationResetPhase.Prepared, result.Value.Phase);

        Assert.False(result.Value.PointOfNoReturn);

        Assert.True(result.Value.ResumeRequired);

        Assert.True(preData.Executed);

        Assert.NotNull(active.Record);

        Assert.Empty(data.ApplyRequests);

    }

    [Fact]
    public async Task Cancellation_after_active_publication_is_checkpointed_as_a_reset_result()
    {

        FakeActiveStore active = new();

        FakePreDataMutation preData = new()
        {
            Exception = new OperationCanceledException(),
        };

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            preDataMutation: preData);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ResumeRequired);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Value.ErrorCode);

        Assert.Equal(InstallationResetPhase.Prepared, result.Value.Phase);

        Assert.NotNull(active.Record);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, active.Record.LastErrorCode);

    }

    [Fact]
    public async Task Cancellation_after_canonical_data_call_is_checkpointed_conservatively()
    {

        FakeDataService data = new(CreateDataPlan("global-data"))
        {
            ApplyException = new OperationCanceledException(),
        };

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ResumeRequired);

        Assert.True(result.Value.PointOfNoReturn);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Value.ErrorCode);

        Assert.NotNull(active.Record);

        Assert.True(active.Record.PointOfNoReturn);

    }

    [Fact]
    public async Task Resume_after_data_checkpoint_keeps_operation_and_skips_data_replay()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new(
            [
                new InstallationResetCredentialSummary(
                    "accepted-account",
                    InstallationResetItemStatus.Pending),
            ]);

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new();

        InstallationResetService service = new(data, credentials, active, cleanup);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        InstallationResetActiveRecord checkpoint = CreateActive(
            plan,
            InstallationResetPhase.DataResetComplete) with
        {
            RowsDeleted = 3,
        };

        active.Seed(checkpoint);

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(checkpoint.OperationId, result.Value.OperationId);

        Assert.Equal(plan.PlanId, result.Value.PlanId);

        Assert.Single(data.PlanRequests);

        Assert.Empty(data.ApplyRequests);

        Assert.Equal(2, credentials.DeleteRequests.Count);

        Assert.All(credentials.DeleteRequests, request =>
            Assert.Equal(["accepted-account"], request));

        Assert.All(active.Writes, write =>
            Assert.Equal(checkpoint.OperationId, write.OperationId));

        Assert.True(active.Retired);

    }

    [Fact]
    public async Task Prepared_resume_uses_the_accepted_data_plan_without_replanning()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        InstallationResetActiveRecord prepared = CreateActive(
            plan,
            InstallationResetPhase.Prepared,
            pointOfNoReturn: false);

        active.Seed(prepared);

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(prepared.OperationId, result.Value.OperationId);

        Assert.Single(data.PlanRequests);

        DataRetentionApplyRequest apply = Assert.Single(data.ApplyRequests);

        Assert.Equal("global-data", apply.ExpectedPlanId);

        Assert.All(active.Writes, write =>
            Assert.Equal(prepared.OperationId, write.OperationId));

    }

    [Fact]
    public async Task Different_expected_plan_cannot_overwrite_the_active_operation()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        InstallationResetActiveRecord checkpoint = CreateActive(
            plan,
            InstallationResetPhase.DataResetComplete);

        active.Seed(checkpoint);

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, "different-plan"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, result.Error.Code);

        Assert.Equal(checkpoint, active.Record);

        Assert.Empty(active.Writes);

        Assert.Empty(data.ApplyRequests);

    }

    [Fact]
    public async Task Changed_workspace_binding_cannot_resume_the_active_operation()
    {

        string root = Path.GetFullPath(Path.Combine("/tmp", "campaign"));

        FakeWorkspaceResolver resolver = new(
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), root));

        FakeDataService data = new(CreateDataPlan("workspace-data"));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: resolver);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Workspace,
            Path.Combine(root, "src"));

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        InstallationResetActiveRecord checkpoint = CreateActive(
            plan,
            InstallationResetPhase.DataResetComplete);

        active.Seed(checkpoint);

        resolver.Bindings =
            [new DataRetentionWorkspaceBinding(Guid.NewGuid(), root)];

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, result.Error.Code);

        Assert.Equal(checkpoint, active.Record);

        Assert.Empty(active.Writes);

        Assert.Empty(data.ApplyRequests);

    }

    [Fact]
    public async Task Completed_unreported_result_is_returned_from_the_record_then_retired()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new([]);

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new();

        InstallationResetService service = new(data, credentials, active, cleanup);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        InstallationResetActiveRecord completed = CreateActive(
            plan,
            InstallationResetPhase.Completed) with
        {
            RowsDeleted = 7,
            FilesDeleted = 5,
            EstimatedBytesDeleted = 11,
            CredentialResults =
            [
                new InstallationResetCredentialResult(
                    "accepted-account",
                    InstallationResetItemStatus.Deleted),
            ],
        };

        active.Seed(completed);

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(completed.OperationId, result.Value.OperationId);

        Assert.Equal(plan.PlanId, result.Value.PlanId);

        Assert.Equal(7, result.Value.RowsDeleted);

        Assert.Equal(5, result.Value.FilesDeleted);

        Assert.Equal(11, result.Value.EstimatedBytesDeleted);

        Assert.True(result.Value.Verification.Succeeded);

        Assert.False(result.Value.ResumeRequired);

        Assert.Empty(active.Writes);

        Assert.Equal(completed.OperationId, Assert.Single(active.RetiredOperationIds));

        Assert.Empty(data.ApplyRequests);

        Assert.Empty(Assert.Single(credentials.DeleteRequests));

        Assert.True(cleanup.Executed);

    }

    [Fact]
    public async Task Completed_unreported_result_rechecks_selected_files_before_retirement()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new()
        {
            Result = Result<InstallationResetOfflineCleanupResult>.Success(
                new InstallationResetOfflineCleanupResult(
                    FilesDeleted: 0,
                    EstimatedBytesDeleted: 0,
                    CredentialResults: [],
                    PreservedBackups: [],
                    Verification: new InstallationResetVerification(
                        false,
                        [
                            new InstallationResetIssueSummary(
                                ErrorCodes.Data.ReconciliationFailed,
                                "A selected reset file remains."),
                        ]))),
        };

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            cleanup);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        active.Seed(CreateActive(plan, InstallationResetPhase.Completed));

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(cleanup.Executed);

        Assert.True(result.Value.ResumeRequired);

        Assert.False(result.Value.Verification.Succeeded);

        Assert.False(active.Retired);

    }

    [Fact]
    public async Task Completed_unreported_result_rechecks_accepted_credentials_before_retirement()
    {

        const string account = "inference-provider-OPENAI-api-key";

        FakeCredentialInventory credentials = new(
        [
            new InstallationResetCredentialSummary(
                account,
                InstallationResetItemStatus.Pending),
        ])
        {
            DeleteResults =
            [
                new InstallationResetCredentialResult(
                    account,
                    InstallationResetItemStatus.Failed,
                    ErrorCodes.Data.ReconciliationFailed),
            ],
        };

        FakeActiveStore active = new();

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("global-data")),
            credentials,
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        active.Seed(CreateActive(plan, InstallationResetPhase.Completed) with
        {
            CredentialResults =
            [
                new InstallationResetCredentialResult(
                    account,
                    InstallationResetItemStatus.Deleted),
            ],
        });

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(result.Value.ResumeRequired);

        Assert.False(result.Value.Verification.Succeeded);

        Assert.Equal([account], Assert.Single(credentials.DeleteRequests));

        Assert.False(active.Retired);

    }

    [Fact]
    public async Task Completed_record_remains_authoritative_when_exact_retirement_fails()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new()
        {
            RetireResult = Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "retirement failed")),
        };

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        InstallationResetActiveRecord completed = CreateActive(
            plan,
            InstallationResetPhase.Completed);

        active.Seed(completed);

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(completed.OperationId, result.Value.OperationId);

        Assert.Equal(plan.PlanId, result.Value.PlanId);

        Assert.True(result.Value.ResumeRequired);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, result.Value.ErrorCode);

        Assert.Equal(completed, active.Record);

        Assert.Empty(active.Writes);

    }

    [Fact]

    public async Task All_resume_after_global_data_reset_uses_accepted_workspace_binding()
    {

        string workspaceRoot = Path.GetFullPath(Path.Combine(
            "/tmp",
            "all-resume-workspace"));

        FakeWorkspaceResolver resolver = new(
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), workspaceRoot));

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: resolver);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.All,
            Path.Combine(workspaceRoot, "src"));

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        active.Seed(CreateActive(
            plan,
            InstallationResetPhase.DataResetComplete));

        resolver.Failure = new Error(
            ErrorCodes.Data.InventoryUnavailable,
            "The Campaign catalog was removed by the accepted global reset.");

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(InstallationResetPhase.Completed, result.Value.Phase);

        Assert.Equal(plan.Workspace, active.Writes[0].Workspace);

        Assert.Empty(data.ApplyRequests);

    }

    [Fact]
    public async Task All_resume_after_global_commit_uses_prepared_accepted_workspace_when_catalog_is_gone()
    {

        string workspaceRoot = Path.GetFullPath(Path.Combine(
            "/tmp",
            "all-prepared-resume-workspace"));

        FakeWorkspaceResolver resolver = new(
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), workspaceRoot));

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: resolver);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.All,
            Path.Combine(workspaceRoot, "src"));

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        active.Seed(CreateActive(
            plan,
            InstallationResetPhase.Prepared,
            pointOfNoReturn: true));

        resolver.Failure = new Error(
            ErrorCodes.Data.InventoryUnavailable,
            "The Campaign catalog was removed by the accepted global reset.");

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(InstallationResetPhase.Completed, result.Value.Phase);

    }

    [Fact]
    public async Task All_resume_rejects_invocation_outside_the_accepted_workspace()
    {

        string workspaceRoot = Path.GetFullPath(Path.Combine(
            "/tmp",
            "all-bound-workspace"));

        FakeWorkspaceResolver resolver = new(
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), workspaceRoot));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: resolver);

        InstallationResetPlanRequest acceptedRequest = new(
            InstallationResetScope.All,
            Path.Combine(workspaceRoot, "src"));

        InstallationResetPlan plan = (await service.PlanAsync(
            acceptedRequest,
            CancellationToken.None)).Value;

        active.Seed(CreateActive(
            plan,
            InstallationResetPhase.DataResetComplete));

        InstallationResetPlanRequest differentRequest = new(
            InstallationResetScope.All,
            Path.Combine("/tmp", "different-workspace"));

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(differentRequest, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, result.Error.Code);

    }

    [Fact]
    public async Task All_resume_rejects_invocation_inside_an_excluded_nested_campaign()
    {

        string workspaceRoot = Path.GetFullPath(Path.Combine(
            "/tmp",
            "all-parent-workspace"));

        string nestedRoot = Path.Combine(workspaceRoot, "nested");

        FakeWorkspaceResolver resolver = new(
            new InstallationResetWorkspaceResolution(
                new DataRetentionWorkspaceBinding(Guid.NewGuid(), workspaceRoot),
                [nestedRoot]));

        FakeActiveStore active = new();

        InstallationResetService service = new(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: resolver);

        InstallationResetPlanRequest acceptedRequest = new(
            InstallationResetScope.All,
            Path.Combine(workspaceRoot, "src"));

        InstallationResetPlan plan = (await service.PlanAsync(
            acceptedRequest,
            CancellationToken.None)).Value;

        plan = plan with
        {
            AcceptedBinding = plan.AcceptedBinding with
            {
                ExcludedRoots = [nestedRoot],
            },
        };

        active.Seed(CreateActive(
            plan,
            InstallationResetPhase.Prepared,
            pointOfNoReturn: true));

        InstallationResetPlanRequest nestedRequest = new(
            InstallationResetScope.All,
            Path.Combine(nestedRoot, "src"));

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(nestedRequest, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, result.Error.Code);

    }

    [Fact]
    public async Task Apply_persists_the_point_of_no_return_before_canonical_data_mutation()
    {

        FakeActiveStore active = new();

        FakeDataService data = new(CreateDataPlan("global-data"))
        {
            BeforeApply = () =>
            {

                Assert.NotNull(active.Record);

                Assert.Equal(InstallationResetPhase.Prepared, active.Record.Phase);

                Assert.True(active.Record.PointOfNoReturn);

            },
        };

        InstallationResetService service = new(
            data,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

    }

    [Fact]
    public async Task Apply_deletes_only_accepted_credentials_and_merges_cleanup_results()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new(
            [
                new InstallationResetCredentialSummary(
                    "accepted-account",
                    InstallationResetItemStatus.Pending),
            ]);

        credentials.DeleteResults =
        [
            new InstallationResetCredentialResult(
                "accepted-account",
                InstallationResetItemStatus.Deleted),
        ];

        FakeOfflineCleanup cleanup = new()
        {
            Result = Result<InstallationResetOfflineCleanupResult>.Success(
                new InstallationResetOfflineCleanupResult(
                    FilesDeleted: 0,
                    EstimatedBytesDeleted: 0,
                    CredentialResults:
                    [
                        new InstallationResetCredentialResult(
                            "cleanup-account",
                            InstallationResetItemStatus.Absent),
                    ],
                    PreservedBackups: [],
                    Verification: new InstallationResetVerification(true, []))),
        };

        InstallationResetService service = new(
            data,
            credentials,
            new FakeActiveStore(),
            cleanup);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, credentials.DeleteRequests.Count);

        Assert.All(credentials.DeleteRequests, request =>
            Assert.Equal(["accepted-account"], request));

        Assert.Equal(
            ["accepted-account", "cleanup-account"],
            result.Value.CredentialResults.Select(static item => item.Account));

    }

    [Fact]
    public async Task Credential_verification_failure_is_durable_and_resumes_without_data_replay()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new(
            [
                new InstallationResetCredentialSummary(
                    "accepted-account",
                    InstallationResetItemStatus.Pending),
            ])
        {
            DeleteResults =
            [
                new InstallationResetCredentialResult(
                    "accepted-account",
                    InstallationResetItemStatus.Failed,
                    ErrorCodes.Data.ReconciliationFailed),
            ],
        };

        FakeActiveStore active = new();

        InstallationResetService service = new(
            data,
            credentials,
            active,
            new FakeOfflineCleanup());

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> first = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(first.IsSuccess);

        Assert.True(first.Value.PointOfNoReturn);

        Assert.True(first.Value.ResumeRequired);

        Assert.False(first.Value.Verification.Succeeded);

        Assert.Equal(
            InstallationResetItemStatus.Failed,
            Assert.Single(first.Value.CredentialResults).Status);

        Assert.NotNull(active.Record);

        Assert.Equal(first.Value.OperationId, active.Record.OperationId);

        credentials.DeleteResults =
        [
            new InstallationResetCredentialResult(
                "accepted-account",
                InstallationResetItemStatus.Deleted),
        ];

        Result<InstallationResetResult> resumed = await service.ApplyAsync(
            new InstallationResetApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(resumed.IsSuccess);

        Assert.Equal(first.Value.OperationId, resumed.Value.OperationId);

        Assert.True(resumed.Value.Verification.Succeeded);

        Assert.False(resumed.Value.ResumeRequired);

        Assert.Single(data.ApplyRequests);

        Assert.Equal(3, credentials.DeleteRequests.Count);

        Assert.True(active.Retired);

    }

    private static async Task<PreparedOnlineHandoffFixture> PrepareOnlineHandoffAsync(
        InstallationResetCredentialSummary[]? credentialInventory = null)
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        FakeDataService data = new(localDataPlan);

        FakeCredentialInventory credentials = new(credentialInventory ?? []);

        FakeActiveStore active = new();

        FakeOfflineCleanup offlineCleanup = new();

        FakePreDataMutation preDataMutation = new();

        InstallationResetService service = new(
            data,
            credentials,
            active,
            offlineCleanup,
            preDataMutation: preDataMutation);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        InstallationResetPlan rebound = service.BindOnlineDataPlan(
            planRequest,
            localPlan,
            localDataPlan with { PlanId = "online-data-plan" }).Value;

        InstallationResetApplyRequest request = new(
            planRequest,
            rebound.PlanId);

        InstallationResetOnlineDataHandoff handoff = (await service.PrepareAsync(
            request,
            rebound,
            CancellationToken.None)).Value;

        return new PreparedOnlineHandoffFixture(
            service,
            data,
            credentials,
            active,
            offlineCleanup,
            preDataMutation,
            request,
            handoff);

    }

    private static DataRetentionApplyResult CreateOnlineDataCompletion(
        InstallationResetOnlineDataHandoff handoff) =>
        new(
            OperationId: Guid.Parse("40404040-4040-4040-8040-404040404040"),
            PlanId: handoff.DataPlanId,
            RowsDeleted: 7,
            FilesDeleted: 3,
            EstimatedBytesDeleted: 19,
            DerivedRecordsDeleted: 2,
            Reconciled: true,
            Blockers: [],
            Conflicts: [],
            RequestedOperationId: handoff.RequestedOperationId);

    private static InstallationResetActiveRecord CreateActive(
        InstallationResetPlan plan,
        InstallationResetPhase phase,
        bool pointOfNoReturn = true) =>
        new(
            InstallationResetActiveStore.CurrentVersion,
            Guid.NewGuid(),
            plan.PlanId,
            plan.Scope,
            plan.Workspace,
            plan.AcceptedBinding,
            phase,
            pointOfNoReturn,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null);

    private sealed record PreparedOnlineHandoffFixture(
        InstallationResetService Service,
        FakeDataService Data,
        FakeCredentialInventory Credentials,
        FakeActiveStore Active,
        FakeOfflineCleanup OfflineCleanup,
        FakePreDataMutation PreDataMutation,
        InstallationResetApplyRequest Request,
        InstallationResetOnlineDataHandoff Handoff);

    private static DataRetentionPlan CreateDataPlan(string planId) =>
        new(
            planId,
            new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
            [
                new DataRetentionPlanItem(
                    RetentionDataClass.WorkspaceChunks,
                    Rows: 3,
                    Files: 0,
                    EstimatedBytes: 0,
                    DerivedRecords: 2),
            ],
            Blockers: [],
            Conflicts: [],
            Rows: 3,
            Files: 0,
            EstimatedBytes: 0,
            DerivedRecords: 2,
            CandidateIds: ["candidate"],
            RequiresConfirmation: true);

    private static DataRetentionPlan MutateOrdinaryPlan(
        DataRetentionPlan plan,
        string dimension) =>
        dimension switch
        {
            "request" => plan with
            {
                Request = new DataRetentionRequest(DataRetentionOperation.Prune),
            },
            "items" => plan with
            {
                Items =
                [
                    plan.Items[0] with { DerivedRecords = 99 },
                ],
            },
            "blockers" => plan with
            {
                Blockers =
                [
                    new DataRetentionBlocker(
                        RetentionDataClass.WorkspaceChunks,
                        "resource",
                        ErrorCodes.Data.Blocked,
                        "blocked"),
                ],
            },
            "conflicts" => plan with
            {
                Conflicts =
                [
                    new DataRetentionConflict("conflict", "resource", "conflict"),
                ],
            },
            "rows" => plan with { Rows = plan.Rows + 1 },
            "files" => plan with { Files = plan.Files + 1 },
            "estimated-bytes" => plan with
            {
                EstimatedBytes = plan.EstimatedBytes + 1,
            },
            "derived-records" => plan with
            {
                DerivedRecords = plan.DerivedRecords + 1,
            },
            "candidate-ids" => plan with { CandidateIds = ["changed"] },
            "requires-confirmation" => plan with
            {
                RequiresConfirmation = !plan.RequiresConfirmation,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };

    private static Result<InstallationResetFileSystemInventory> InventoryWithIdentity(
        string identity) =>
        Result<InstallationResetFileSystemInventory>.Success(
            new InstallationResetFileSystemInventory(
                [
                    new InstallationResetTargetDescriptor(
                        "installation-file",
                        InstallationResetTargetRole.FileSystem,
                        "/state/arcanum.json",
                        "/state/arcanum.json",
                        DatabasePredicate: null,
                        new InstallationResetFileIdentity(identity, 5, 1),
                        Rows: null,
                        Files: 1,
                        EstimatedBytes: 5),
                ],
                PreservedBackups: [],
                Exclusions: [],
                Files: 1,
                EstimatedBytes: 5));

    private sealed class FakeDataService(params DataRetentionPlan[] plans)
        : IInstallationResetDataService
    {

        private int _planIndex;

        public List<InstallationResetDataPlanRequest> PlanRequests { get; } = [];

        public List<DataRetentionApplyRequest> ApplyRequests { get; } = [];

        public Result<DataRetentionApplyResult>? ApplyResult { get; set; }

        public Action? BeforeApply { get; set; }

        public Exception? ApplyException { get; set; }

        public Task<Result<DataRetentionPlan>> PlanAsync(
            InstallationResetDataPlanRequest request,
            CancellationToken cancellationToken = default)
        {

            PlanRequests.Add(request);

            DataRetentionPlan plan = plans[Math.Min(_planIndex, plans.Length - 1)];

            _planIndex++;

            return Task.FromResult(Result<DataRetentionPlan>.Success(plan));

        }

        public Task<Result<DataRetentionApplyResult>> ApplyAsync(
            DataRetentionApplyRequest request,
            CancellationToken cancellationToken = default)
        {

            ApplyRequests.Add(request);

            BeforeApply?.Invoke();

            if (ApplyException is { } exception)
            {

                throw exception;

            }

            if (ApplyResult is { } configured)
            {

                return Task.FromResult(configured);

            }

            return Task.FromResult(Result<DataRetentionApplyResult>.Success(
                new DataRetentionApplyResult(
                    Guid.NewGuid(),
                    request.ExpectedPlanId!,
                    RowsDeleted: 3,
                    FilesDeleted: 0,
                    EstimatedBytesDeleted: 0,
                    DerivedRecordsDeleted: 2,
                    Reconciled: true,
                    Blockers: [],
                    Conflicts: [])));

        }

    }

    private sealed class FakeCredentialInventory(
        InstallationResetCredentialSummary[] inventory)
        : IInstallationResetCredentialService
    {

        public List<string[]> DeleteRequests { get; } = [];

        public InstallationResetCredentialResult[]? DeleteResults { get; set; }

        public InstallationResetCredentialSummary[] Probe() => inventory;

        public InstallationResetCredentialResult[] DeleteAndVerify(string[] accounts)
        {

            DeleteRequests.Add([.. accounts]);

            return DeleteResults ??
                [.. accounts.Select(account =>
                {

                    InstallationResetCredentialSummary? item = inventory.SingleOrDefault(
                        candidate => string.Equals(
                            candidate.Account,
                            account,
                            StringComparison.Ordinal));

                    return new InstallationResetCredentialResult(
                        account,
                        item?.Status is InstallationResetItemStatus.Pending
                            ? InstallationResetItemStatus.Deleted
                            : item?.Status ?? InstallationResetItemStatus.Absent,
                        item?.ErrorCode);

                })];

        }

    }

    private sealed class FakeActiveStore : IInstallationResetActiveStore
    {

        public bool Written => Writes.Count > 0;

        public bool Retired { get; private set; }

        public InstallationResetActiveRecord? Record { get; private set; }

        public List<InstallationResetActiveRecord> Writes { get; } = [];

        public Func<InstallationResetActiveRecord, Result>? WriteOverride { get; set; }

        public List<Guid> RetiredOperationIds { get; } = [];

        public Result RetireResult { get; set; } = Result.Success();

        public void Seed(InstallationResetActiveRecord record)
        {

            Record = record;

        }

        public Task<Result<InstallationResetActiveRecord?>> ReadAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<InstallationResetActiveRecord?>.Success(Record));

        public Task<Result> WriteAsync(
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken)
        {

            Writes.Add(record);

            if (WriteOverride is { } writeOverride)
            {

                Result overridden = writeOverride(record);

                if (overridden.IsFailure)
                {

                    return Task.FromResult(overridden);

                }

            }

            Record = record;

            return Task.FromResult(Result.Success());

        }

        public Task<Result> RetireAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {

            Retired = true;

            RetiredOperationIds.Add(operationId);

            if (RetireResult.IsFailure)
            {

                return Task.FromResult(RetireResult);

            }

            Record = null;

            return Task.FromResult(Result.Success());

        }

        public Task<Result> RetirePreEffectAsync(
            InstallationResetOnlineDataHandoff handoff,
            CancellationToken cancellationToken) =>
            RetireAsync(handoff.RequestedOperationId, cancellationToken);

    }

    private sealed class FakeOfflineCleanup : IInstallationResetOfflineCleanup
    {

        public bool Executed => Plans.Count > 0;

        public List<InstallationResetPlan> Plans { get; } = [];

        public List<string[]> PlanSelectedRoots { get; } = [];

        public List<string[]> PlanExcludedRoots { get; } = [];

        public Result<InstallationResetFileSystemInventory> Inventory { get; set; } =
            Result<InstallationResetFileSystemInventory>.Success(
                new InstallationResetFileSystemInventory([], [], [], 0, 0));

        public Result<InstallationResetOfflineCleanupResult> Result { get; set; } =
            Result<InstallationResetOfflineCleanupResult>.Success(
                new InstallationResetOfflineCleanupResult(
                    FilesDeleted: 0,
                    EstimatedBytesDeleted: 0,
                    CredentialResults: [],
                    PreservedBackups: [],
                    Verification: new InstallationResetVerification(true, [])));

        public Task<Result<InstallationResetOfflineCleanupResult>> ExecuteAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken)
        {

            Plans.Add(plan);

            return Task.FromResult(Result);

        }

        public Task<Result<InstallationResetFileSystemInventory>> PlanAsync(
            string[] selectedRoots,
            string[] excludedRoots,
            CancellationToken cancellationToken)
        {

            PlanSelectedRoots.Add([.. selectedRoots]);

            PlanExcludedRoots.Add([.. excludedRoots]);

            return Task.FromResult(Inventory);

        }

    }

    private sealed class FixedStateRoots(string[] roots) : IInstallationResetStateRoots
    {

        public string[] Resolve(
            InstallationResetScope scope,
            DataRetentionWorkspaceBinding? workspace) => [.. roots];

    }

    private sealed class FakePreDataMutation : IInstallationResetPreDataMutation
    {

        public bool Executed { get; private set; }

        public Result Result { get; set; } = Result.Success();

        public Exception? Exception { get; set; }

        public Task<Result> ExecuteAsync(CancellationToken cancellationToken)
        {

            Executed = true;

            if (Exception is { } exception)
            {

                throw exception;

            }

            return Task.FromResult(Result);

        }

    }

    private sealed class FakeWorkspaceResolver(
        params InstallationResetWorkspaceResolution[] resolutions)
        : IInstallationResetWorkspaceResolver
    {

        public FakeWorkspaceResolver(params DataRetentionWorkspaceBinding[] bindings)
            : this(
                [.. bindings.Select(static binding =>
                    new InstallationResetWorkspaceResolution(binding, []))])
        {

        }

        public InstallationResetWorkspaceResolution[] Resolutions { get; set; } = resolutions;

        public DataRetentionWorkspaceBinding[] Bindings
        {

            get => [.. Resolutions.Select(static resolution => resolution.Workspace)];

            set => Resolutions =
                [.. value.Select(static binding =>
                    new InstallationResetWorkspaceResolution(binding, []))];

        }

        public Error? Failure { get; set; }

        public Task<Result<InstallationResetWorkspaceResolution>> ResolveAsync(
            string invocationDirectory,
            CancellationToken cancellationToken)
        {

            if (Failure is { } failure)
            {

                return Task.FromResult(
                    Result<InstallationResetWorkspaceResolution>.Failure(failure));

            }

            return Task.FromResult(Result<InstallationResetWorkspaceResolution>.Success(
                Resolutions
                    .Where(resolution => Path.GetFullPath(invocationDirectory).StartsWith(
                        resolution.Workspace.WorkspaceRoot + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                    .OrderByDescending(static resolution =>
                        resolution.Workspace.WorkspaceRoot.Length)
                    .First()));

        }

    }

}
