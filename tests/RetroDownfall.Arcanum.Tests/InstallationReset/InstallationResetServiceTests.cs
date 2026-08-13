using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetServiceTests
{

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
