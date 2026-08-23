using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class InstallationResetServiceTests
{

    [Fact]
    public async Task BindOnlineDataPlan_rebinds_only_the_Covenant_aware_data_identity()
    {

        DataRetentionPlan localDataPlan = CreateDataPlan("local-data-plan");

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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
    public async Task Workspace_apply_preserves_global_daemon_registration()
    {

        DataRetentionWorkspaceBinding workspace = new(
            Guid.Parse("50505050-5050-5050-5050-505050505050"),
            "/workspace");

        FakePreDataMutation preDataMutation = new();

        FakeDataService data = new(CreateDataPlan("workspace-data"));

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> applied = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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

        InstallationResetService service = CreateService(
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
    public async Task Binding_and_plan_ids_distinguish_delimiter_ambiguous_exclusion_sets()
    {

        InstallationResetPlan first = await PlanWithInventoryAsync(
            new InstallationResetFileSystemInventory(
                Targets: [],
                PreservedBackups: [],
                Exclusions:
                [
                    new InstallationResetExclusion("campaign", "/a", "excluded"),
                    new InstallationResetExclusion("campaign", "/b,/c", "excluded"),
                ],
                Files: 0,
                EstimatedBytes: 0));

        InstallationResetPlan second = await PlanWithInventoryAsync(
            new InstallationResetFileSystemInventory(
                Targets: [],
                PreservedBackups: [],
                Exclusions:
                [
                    new InstallationResetExclusion("campaign", "/a,/b", "excluded"),
                    new InstallationResetExclusion("campaign", "/c", "excluded"),
                ],
                Files: 0,
                EstimatedBytes: 0));

        Assert.NotEqual(
            first.AcceptedBinding.ExcludedRoots,
            second.AcceptedBinding.ExcludedRoots);

        Assert.NotEqual(
            first.AcceptedBinding.BindingId,
            second.AcceptedBinding.BindingId);

        Assert.NotEqual(first.PlanId, second.PlanId);

    }

    [Fact]
    public async Task Plan_ids_distinguish_delimiter_ambiguous_target_fields()
    {

        InstallationResetPlan first = await PlanWithInventoryAsync(
            InventoryWithTarget(
                resourceId: "file:one",
                canonicalPath: "/state/two"));

        InstallationResetPlan second = await PlanWithInventoryAsync(
            InventoryWithTarget(
                resourceId: "file",
                canonicalPath: "one:/state/two"));

        Assert.Equal(
            first.AcceptedBinding.BindingId,
            second.AcceptedBinding.BindingId);

        Assert.NotEqual(first.Targets, second.Targets);

        Assert.NotEqual(first.PlanId, second.PlanId);

    }

    [Fact]
    public async Task Apply_replans_before_active_publication_and_binds_the_expected_plan()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new([]);

        FakeActiveStore active = new();

        FakeOfflineCleanup cleanup = new();

        InstallationResetService service = CreateService(data, credentials, active, cleanup);

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            request,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(data, credentials, active, cleanup);

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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(data, credentials, active, cleanup);

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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(service,
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

        InstallationResetService service = CreateService(
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

        Result<InstallationResetResult> first = await ApplyUnderTestLockAsync(service,
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

        Result<InstallationResetResult> resumed = await ApplyUnderTestLockAsync(service,
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


    [Theory]
    [InlineData(HostProcessToolsMarkerPairDisposition.PendingBlocked)]
    [InlineData(HostProcessToolsMarkerPairDisposition.TaintedMatched)]
    [InlineData(HostProcessToolsMarkerPairDisposition.MismatchBlocked)]
    public async Task Global_plan_adds_one_content_free_external_remediation_blocker_for_dangerous_pair(
        HostProcessToolsMarkerPairDisposition disposition)
    {

        const string secret = "taint-evidence-that-must-not-escape";

        FakePairReader pairReader = new(JoinResult(disposition));

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: pairReader);

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/invocation/" + secret),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        InstallationResetIssueSummary blocker = Assert.Single(
            result.Value.Blockers,
            static candidate =>
                candidate.Code == ErrorCodes.Data.ExternalRemediationRequired);

        Assert.Null(blocker.ResourceId);

        Assert.DoesNotContain(secret, blocker.Message, StringComparison.Ordinal);

        Assert.Equal(1, pairReader.ReadCount);

    }

    [Fact]
    public async Task Workspace_plan_does_not_read_host_process_tools_pair()
    {

        DataRetentionWorkspaceBinding workspace = new(
            Guid.Parse("50505050-5050-5050-8050-505050505050"),
            "/workspace");

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.MismatchBlocked))
        {
            Exception = new InvalidOperationException(
                "Workspace planning must not inspect installation taint evidence."),
        };

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("workspace-data")),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup(),
            workspaceResolver: new FakeWorkspaceResolver(workspace),
            stateRoots: new FixedStateRoots(["/workspace"]),
            pairReader: pairReader);

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Workspace,
                "/workspace/child"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, pairReader.ReadCount);

    }

    [Fact]
    public async Task Ordinary_apply_rechecks_pair_and_refuses_dangerous_state_before_effects()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new([]);

        FakeActiveStore active = new();

        FakeOfflineCleanup offline = new();

        FakePreDataMutation preData = new();

        FakePairReader pairReader = new(
            JoinResult(HostProcessToolsMarkerPairDisposition.Clean),
            JoinResult(HostProcessToolsMarkerPairDisposition.TaintedMatched));

        InstallationResetService service = CreateService(
            data,
            credentials,
            active,
            offline,
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            preDataMutation: preData,
            pairReader: pairReader);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.Global,
            "/invocation");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(
            service,
            new InstallationResetApplyRequest(planRequest, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationRequired, result.Error.Code);

        Assert.Equal(2, pairReader.ReadCount);

        Assert.Equal(0, active.IdentityReadCount);

        Assert.Equal(0, active.RecoverCount);

        Assert.Empty(active.Writes);

        Assert.Empty(data.ApplyRequests);

        Assert.False(preData.Executed);

        Assert.False(offline.Executed);

        Assert.Empty(credentials.DeleteRequests);

    }

    [Fact]
    public void Service_construction_requires_a_host_process_tools_pair_reader()
    {

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() =>
            new InstallationResetService(
                new FakeDataService(CreateDataPlan("global-data")),
                new FakeCredentialInventory([]),
                new FakeActiveStore(),
                new FakeOfflineCleanup(),
                pairReader: null));

        Assert.Equal("pairReader", exception.ParamName);

    }

    [Fact]
    public async Task Ordinary_locked_apply_rejects_an_authenticated_full_claim_before_effects()
    {

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new([]);

        FakeActiveStore active = new();

        FakeOfflineCleanup offline = new();

        FakePreDataMutation preData = new();

        InstallationResetService service = CreateService(
            data,
            credentials,
            active,
            offline,
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            preDataMutation: preData,
            pairReader: CleanPairReader());

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        InstallationResetActiveRecord claimed = CreateActive(
            plan,
            InstallationResetPhase.Prepared,
            pointOfNoReturn: false);

        active.Seed(claimed with
        {
            FullInstallationResetRemediationClaim = RemediationClaim(
                claimed.OperationId),
        });

        Result<InstallationResetResult> result = await ApplyUnderTestLockAsync(
            service,
            new InstallationResetApplyRequest(planRequest, plan.PlanId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(
            ErrorCodes.Data.ExternalRemediationRequired,
            result.Error.Code);

        Assert.Empty(active.Writes);

        Assert.Empty(data.ApplyRequests);

        Assert.False(preData.Executed);

        Assert.False(offline.Executed);

        Assert.Empty(credentials.DeleteRequests);

    }

    [Fact]
    public async Task Full_apply_rejects_request_operation_mismatch_before_any_dependency()
    {

        Guid signedOperationId = Guid.Parse("12121212-1212-4121-8121-121212121212");

        FakeActiveStore active = new();

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.TaintedMatched))
        {
            Exception = new InvalidOperationException("Pair I/O must not run."),
        };

        FakeRemediationVerifier verifier = new(
            Authorization(signedOperationId))
        {
            Exception = new InvalidOperationException("Verification must not run."),
        };

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            pairReader: pairReader,
            remediationVerifier: verifier);

        FullInstallationResetRequest request = FullRequest(
            signedOperationId,
            expectedPlanId: "confirmed-plan") with
        {
            OperationId = Guid.Parse("34343434-3434-4343-8343-343434343434"),
        };

        Result<InstallationResetResult> result = await service.ApplyFullAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, result.Error.Code);

        Assert.Equal(0, pairReader.ReadCount);

        Assert.Equal(0, verifier.VerifyCount);

        Assert.Equal(0, active.IdentityReadCount);

        Assert.Equal(0, active.RecoverCount);

    }

    [Fact]
    public async Task Full_apply_with_matching_operation_requires_the_exact_locked_control_path()
    {

        Guid operationId = Guid.Parse("35353535-3535-4353-8353-353535353535");

        FakeActiveStore active = new();

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.TaintedMatched))
        {
            Exception = new InvalidOperationException("Pair I/O must not run."),
        };

        FakeRemediationVerifier verifier = new(Authorization(operationId))
        {
            Exception = new InvalidOperationException("Verification must not run."),
        };

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            pairReader: pairReader,
            remediationVerifier: verifier);

        Result<InstallationResetResult> result = await service.ApplyFullAsync(
            FullRequest(operationId, expectedPlanId: "confirmed-plan"),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, result.Error.Code);

        Assert.Equal(0, pairReader.ReadCount);

        Assert.Equal(0, verifier.VerifyCount);

        Assert.Equal(0, verifier.MatchCount);

        Assert.Equal(0, active.IdentityReadCount);

        Assert.Equal(0, active.RecoverCount);

    }

    [Fact]
    public async Task Full_locked_apply_requires_the_exact_held_installation_lock()
    {

        Guid operationId = Guid.Parse("45454545-4545-4545-8545-454545454545");

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.TaintedMatched));

        FakeRemediationVerifier verifier = new(Authorization(operationId));

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            new FakeOfflineCleanup(),
            pairReader: pairReader,
            remediationVerifier: verifier);

        string unrelatedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-reset-unrelated-{Guid.NewGuid():N}");

        RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions
            .CreateOwnerOnlyDirectoryAtPath(unrelatedRoot);

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(unrelatedRoot);

        using ArcanumMaintenanceLock unrelatedLock = acquired.BorrowAcquiredLock();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyFullUnderMaintenanceLockAsync(
                FullRequest(operationId, "confirmed-plan"),
                unrelatedLock,
                CancellationToken.None));

        Assert.Equal(0, pairReader.ReadCount);

        Assert.Equal(0, verifier.VerifyCount);

    }

    [Fact]
    public async Task Full_locked_apply_rejects_non_all_scope_before_lock_or_dependency_access()
    {

        Guid operationId = Guid.Parse("46464646-4646-4464-8464-464646464646");

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.TaintedMatched))
        {
            Exception = new InvalidOperationException("Pair I/O must not run."),
        };

        FakeRemediationVerifier verifier = new(Authorization(operationId))
        {
            Exception = new InvalidOperationException("Verification must not run."),
        };

        FakeActiveStore active = new();

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            pairReader: pairReader,
            remediationVerifier: verifier);

        FullInstallationResetRequest invalid = FullRequest(
            operationId,
            expectedPlanId: "confirmed-plan") with
        {
            Apply = new InstallationResetApplyRequest(
                new InstallationResetPlanRequest(
                    InstallationResetScope.Global,
                    "/invocation"),
                "confirmed-plan"),
        };

        string unrelatedRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-reset-unrelated-{Guid.NewGuid():N}");

        RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions
            .CreateOwnerOnlyDirectoryAtPath(unrelatedRoot);

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(unrelatedRoot);

        using ArcanumMaintenanceLock unrelatedLock = acquired.BorrowAcquiredLock();

        Result<InstallationResetResult> result =
            await service.ApplyFullUnderMaintenanceLockAsync(
                invalid,
                unrelatedLock,
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, result.Error.Code);

        Assert.Equal(0, pairReader.ReadCount);

        Assert.Equal(0, verifier.VerifyCount);

        Assert.Equal(0, verifier.MatchCount);

        Assert.Equal(0, active.IdentityReadCount);

        Assert.Equal(0, active.RecoverCount);

    }

    [Fact]
    public async Task Full_locked_apply_publishes_authenticated_claim_and_runs_no_reset_effects()
    {

        Guid operationId = Guid.Parse("56565656-5656-4565-8565-565656565656");

        FakeDataService data = new(CreateDataPlan("global-data"));

        FakeCredentialInventory credentials = new([]);

        FakeActiveStore active = new();

        FakeOfflineCleanup offline = new();

        FakePreDataMutation preData = new();

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.TaintedMatched));

        FullInstallationResetRemediationAuthorization authorization =
            Authorization(operationId);

        FakeRemediationVerifier verifier = new(authorization);

        InstallationResetService service = CreateService(
            data,
            credentials,
            active,
            offline,
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            preDataMutation: preData,
            pairReader: pairReader,
            remediationVerifier: verifier);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        FullInstallationResetRequest request = FullRequest(
            operationId,
            plan.PlanId,
            planRequest);

        Result<InstallationResetResult> result = await ApplyFullUnderTestLockAsync(
            service,
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(operationId, result.Value.OperationId);

        Assert.Equal(InstallationResetPhase.Prepared, result.Value.Phase);

        Assert.True(result.Value.ResumeRequired);

        Assert.False(result.Value.Verification.Succeeded);

        Assert.Equal(
            ErrorCodes.Data.RecoveryRequired,
            Assert.Single(result.Value.Verification.RemainingIssues).Code);

        InstallationResetActiveRecord published = Assert.Single(active.Writes);

        Assert.Equal(operationId, published.OperationId);

        Assert.Equal(InstallationResetScope.All, published.Scope);

        Assert.Equal(InstallationResetPhase.Prepared, published.Phase);

        FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
            FullInstallationResetRemediationClaimV1>(
                published.FullInstallationResetRemediationClaim);

        Assert.Equal((byte)1, claim.Version);

        Assert.Equal(authorization.OperationId, claim.OperationId);

        Assert.Equal(authorization.InstallationId, claim.InstallationId);

        Assert.Equal(authorization.AttestationDigest, claim.AttestationDigest);

        Assert.Equal(authorization.NonceDigest, claim.NonceDigest);

        Assert.Equal(authorization.IssuerDigest, claim.IssuerDigest);

        Assert.Equal(authorization.AcceptedAtUtc, claim.AcceptedAtUtc);

        Assert.Empty(data.ApplyRequests);

        Assert.False(preData.Executed);

        Assert.False(offline.Executed);

        Assert.Empty(credentials.DeleteRequests);

    }

    [Fact]
    public async Task Full_locked_apply_rejects_a_pair_that_changes_during_replanning()
    {

        Guid operationId = Guid.Parse("59595959-5959-4595-8595-595959595959");

        FakeActiveStore active = new();

        FakePairReader pairReader = new(
            JoinResult(HostProcessToolsMarkerPairDisposition.TaintedMatched),
            JoinResult(HostProcessToolsMarkerPairDisposition.TaintedMatched),
            JoinResult(HostProcessToolsMarkerPairDisposition.MismatchBlocked));

        FakeRemediationVerifier verifier = new(Authorization(operationId));

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: pairReader,
            remediationVerifier: verifier);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        Result<InstallationResetResult> result = await ApplyFullUnderTestLockAsync(
            service,
            FullRequest(operationId, plan.PlanId, planRequest),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(
            ErrorCodes.Data.ExternalRemediationRequired,
            result.Error.Code);

        Assert.Equal(0, verifier.VerifyCount);

        Assert.Equal(0, verifier.MatchCount);

        Assert.Empty(active.Writes);

    }

    [Fact]
    public async Task Full_locked_apply_requires_and_preserves_the_confirmed_online_rebound_plan()
    {

        Guid operationId = Guid.Parse("62626262-6262-4626-8626-626262626262");

        DataRetentionPlan localData = CreateDataPlan("local-data");

        FakeActiveStore active = new();

        InstallationResetService service = CreateService(
            new FakeDataService(localData),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: new FakePairReader(JoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched)),
            remediationVerifier: new FakeRemediationVerifier(
                Authorization(operationId)));

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan localPlan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        DataRetentionPlan onlineData = localData with
        {
            PlanId = "online-covenant-plan",
            GeneratedAt = localData.GeneratedAt.AddMinutes(1),
        };

        InstallationResetPlan rebound = service.BindOnlineDataPlan(
            planRequest,
            localPlan,
            onlineData).Value;

        Result<InstallationResetResult> result = await ApplyFullUnderTestLockAsync(
            service,
            FullRequest(operationId, rebound.PlanId, planRequest),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        InstallationResetActiveRecord published = Assert.Single(active.Writes);

        Assert.Equal(rebound.PlanId, published.PlanId);

        Assert.Equal(
            [onlineData.PlanId],
            published.AcceptedBinding.DataPlanIds);

    }

    [Fact]
    public async Task Full_locked_apply_accepts_exact_admitted_claim_without_reverification()
    {

        Guid operationId = Guid.Parse("67676767-6767-4676-8676-676767676767");

        FakeActiveStore active = new();

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.TaintedMatched));

        FakeRemediationVerifier verifier = new(Authorization(operationId));

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: pairReader,
            remediationVerifier: verifier);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        FullInstallationResetRequest request = FullRequest(
            operationId,
            plan.PlanId,
            planRequest);

        Result<InstallationResetResult> first = await ApplyFullUnderTestLockAsync(
            service,
            request,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        FakeDataService retryData = new(CreateDataPlan("must-not-replan"));

        FakeRemediationVerifier retryVerifier = new(Authorization(operationId))
        {
            Exception = new InvalidOperationException(
                "An admitted exact claim must not be reverified after expiry."),

        };

        InstallationResetService restarted = new(
            retryData,
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            pairReader: new FakePairReader(JoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched)),
            remediationVerifier: retryVerifier);

        Result<InstallationResetResult> retry = await ApplyFullUnderTestLockAsync(
            restarted,
            request,
            CancellationToken.None);

        Assert.True(retry.IsSuccess, retry.Error.Message);

        Assert.Equal(first.Value.OperationId, retry.Value.OperationId);

        Assert.Equal(1, verifier.VerifyCount);

        Assert.Equal(0, retryVerifier.VerifyCount);

        Assert.Equal(1, retryVerifier.MatchCount);

        Assert.Empty(retryData.PlanRequests);

        Assert.Single(active.Writes);

    }

    [Fact]
    public async Task Full_locked_apply_rejects_an_advanced_record_as_an_admission_retry()
    {

        Guid operationId = Guid.Parse("71717171-7171-4717-8171-717171717171");

        FakeActiveStore active = new();

        FakeRemediationVerifier verifier = new(Authorization(operationId));

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: new FakePairReader(JoinResult(
                HostProcessToolsMarkerPairDisposition.TaintedMatched)),
            remediationVerifier: verifier);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        FullInstallationResetRequest request = FullRequest(
            operationId,
            plan.PlanId,
            planRequest);

        Result<InstallationResetResult> admitted = await ApplyFullUnderTestLockAsync(
            service,
            request,
            CancellationToken.None);

        Assert.True(admitted.IsSuccess, admitted.Error.Message);

        active.Seed(active.Record! with
        {
            Phase = InstallationResetPhase.DataResetComplete,
            PointOfNoReturn = true,
            RowsDeleted = 1,
        });

        Result<InstallationResetResult> retry = await ApplyFullUnderTestLockAsync(
            service,
            request,
            CancellationToken.None);

        Assert.True(retry.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, retry.Error.Code);

        Assert.Equal(1, verifier.VerifyCount);

        Assert.Equal(0, verifier.MatchCount);

        Assert.Single(active.Writes);

    }

    [Fact]
    public async Task Full_locked_apply_rejects_a_different_claim_for_an_active_operation()
    {

        Guid operationId = Guid.Parse("78787878-7878-4787-8787-787878787878");

        FakeActiveStore active = new();

        FakePairReader pairReader = new(JoinResult(
            HostProcessToolsMarkerPairDisposition.TaintedMatched));

        FullInstallationResetRemediationAuthorization authorization =
            Authorization(operationId);

        FakeRemediationVerifier verifier = new(authorization);

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            active,
            new FakeOfflineCleanup(),
            workspaceResolver: FullWorkspaceResolver(),
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: pairReader,
            remediationVerifier: verifier);

        InstallationResetPlanRequest planRequest = new(
            InstallationResetScope.All,
            "/invocation/child");

        InstallationResetPlan plan = (await service.PlanAsync(
            planRequest,
            CancellationToken.None)).Value;

        FullInstallationResetRequest request = FullRequest(
            operationId,
            plan.PlanId,
            planRequest);

        Result<InstallationResetResult> first = await ApplyFullUnderTestLockAsync(
            service,
            request,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        verifier.Authorization = Authorization(
            operationId,
            nonceDigest: Digest(99));

        verifier.ClaimMatches = false;

        FullInstallationResetRequest changed = request with
        {
            ExternalRemediation = request.ExternalRemediation with
            {
                NonceBase64Url = "ERITFBUWFxgZGhscHR4fIA",
            },
        };

        Result<InstallationResetResult> second = await ApplyFullUnderTestLockAsync(
            service,
            changed,
            CancellationToken.None);

        Assert.True(second.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, second.Error.Code);

        Assert.Equal(1, verifier.VerifyCount);

        Assert.Equal(1, verifier.MatchCount);

        Assert.Single(active.Writes);

        Guid differentOperationId = Guid.Parse(
            "89898989-8989-4898-8989-898989898989");

        FullInstallationResetRequest crossOperation = request with
        {
            OperationId = differentOperationId,
            ExternalRemediation = request.ExternalRemediation with
            {
                OperationId = differentOperationId,
            },
        };

        Result<InstallationResetResult> crossOperationResult =
            await ApplyFullUnderTestLockAsync(
                service,
                crossOperation,
                CancellationToken.None);

        Assert.True(crossOperationResult.IsFailure);

        Assert.Equal(
            ErrorCodes.Data.ExternalRemediationInvalid,
            crossOperationResult.Error.Code);

        Assert.Equal(1, verifier.MatchCount);

        Assert.Single(active.Writes);

    }


    private static InstallationResetService CreateService(
        IInstallationResetDataService dataService,
        IInstallationResetCredentialService credentialService,
        IInstallationResetActiveStore activeStore,
        IInstallationResetOfflineCleanup offlineCleanup,
        TimeProvider? timeProvider = null,
        IInstallationResetWorkspaceResolver? workspaceResolver = null,
        IInstallationResetStateRoots? stateRoots = null,
        IInstallationResetPreDataMutation? preDataMutation = null,
        InstallationResetControlPaths? controlPaths = null,
        IInstallationResetDatabaseIdentityReader? identityReader = null,
        IInstallationResetHostProcessToolsPairReader? pairReader = null,
        IFullInstallationResetRemediationAttestationVerifier? remediationVerifier = null) =>
        new(
            dataService,
            credentialService,
            activeStore,
            offlineCleanup,
            timeProvider,
            workspaceResolver,
            stateRoots,
            preDataMutation,
            controlPaths,
            identityReader,
            pairReader ?? CleanPairReader(),
            remediationVerifier);

    private static async Task<Result<InstallationResetResult>>
        ApplyUnderTestLockAsync(
            InstallationResetService service,
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken = default)
    {

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(service.GuardedRoot);

        using ArcanumMaintenanceLock heldInstallationLock =
            acquired.BorrowAcquiredLock();

        return await service.ApplyUnderMaintenanceLockAsync(
            request,
            heldInstallationLock,
            cancellationToken).ConfigureAwait(false);

    }

    private static async Task<InstallationResetPlan> PlanWithInventoryAsync(
        InstallationResetFileSystemInventory inventory)
    {

        FakeOfflineCleanup cleanup = new()
        {
            Inventory = Result<InstallationResetFileSystemInventory>.Success(inventory),
        };

        InstallationResetService service = CreateService(
            new FakeDataService(CreateDataPlan("global-data")),
            new FakeCredentialInventory([]),
            new FakeActiveStore(),
            cleanup,
            stateRoots: new FixedStateRoots(["/state"]),
            pairReader: CleanPairReader());

        Result<InstallationResetPlan> planned = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/invocation"),
            CancellationToken.None);

        Assert.True(planned.IsSuccess, planned.Error.Message);

        return planned.Value;

    }

    private static async Task<Result<InstallationResetResult>>
        ApplyFullUnderTestLockAsync(
            InstallationResetService service,
            FullInstallationResetRequest request,
            CancellationToken cancellationToken = default)
    {

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(service.GuardedRoot);

        using ArcanumMaintenanceLock heldInstallationLock =
            acquired.BorrowAcquiredLock();

        return await service.ApplyFullUnderMaintenanceLockAsync(
            request,
            heldInstallationLock,
            cancellationToken).ConfigureAwait(false);

    }

    private static FullInstallationResetRequest FullRequest(
        Guid operationId,
        string expectedPlanId,
        InstallationResetPlanRequest? planRequest = null)
    {

        HostProcessToolsMatchedPair pair = MatchedPair();

        FullInstallationResetExternalRemediationAttestation attestation = new(
            Version: 1,
            operationId,
            Guid.Parse("40404040-4040-4040-8040-404040404040"),
            pair.Database.TransitionId!.Value,
            pair.Database.TaintMasterKeyVersion!.Value,
            pair.Database.TaintFingerprint!.Value,
            pair.Database.DatabaseMarkerDigest,
            pair.OsMarker.MarkerBytesDigest,
            Digest(4),
            "AQIDBAUGBwgJCgsMDQ4PEA",
            "RetroDownfall.Remediation.v1",
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
            "signature");

        return new FullInstallationResetRequest(
            operationId,
            new InstallationResetApplyRequest(
                planRequest ?? new InstallationResetPlanRequest(
                    InstallationResetScope.All,
                    "/invocation"),
                expectedPlanId),
            attestation);

    }

    private static FullInstallationResetRemediationAuthorization Authorization(
        Guid operationId,
        CovenantDigest? nonceDigest = null) =>
        new(
            operationId,
            Guid.Parse("40404040-4040-4040-8040-404040404040"),
            Digest(7),
            nonceDigest ?? Digest(8),
            Digest(9),
            new DateTimeOffset(2026, 8, 22, 12, 1, 0, TimeSpan.Zero));

    private static FullInstallationResetRemediationClaimV1 RemediationClaim(
        Guid operationId) =>
        new(
            Version: 1,
            operationId,
            InstallationId: Guid.Parse("40404040-4040-4040-8040-404040404040"),
            AttestationDigest: Digest(7),
            NonceDigest: Digest(8),
            IssuerDigest: Digest(9),
            AcceptedAtUtc: new DateTimeOffset(
                2026,
                8,
                22,
                12,
                1,
                0,
                TimeSpan.Zero));

    private static HostProcessToolsMarkerPairJoinResult JoinResult(
        HostProcessToolsMarkerPairDisposition disposition) =>
        new(
            disposition,
            disposition is HostProcessToolsMarkerPairDisposition.TaintedMatched
                ? MatchedPair()
                : null);

    private static FakePairReader CleanPairReader() =>
        new(JoinResult(HostProcessToolsMarkerPairDisposition.Clean));

    private static HostProcessToolsMatchedPair MatchedPair()
    {

        const string installationIdentity = "installation-identity";

        Guid transitionId = Guid.Parse("91919191-9191-4191-8191-919191919191");

        CovenantDigest fingerprint = Digest(1);

        HostProcessToolsDatabaseMarkerEvidence database = new(
            installationIdentity,
            Core.Security.CovenantHostToolsState.HostToolsTainted,
            transitionId,
            taintMasterKeyVersion: ulong.MaxValue,
            fingerprint);

        HostProcessToolsOsMarkerEvidence osMarker = new(
            installationIdentity,
            transitionId,
            taintMasterKeyVersion: ulong.MaxValue,
            fingerprint,
            markerBytesDigest: Digest(2),
            durableIdentityDigest: Digest(3));

        return new HostProcessToolsMatchedPair(database, osMarker);

    }

    private static CovenantDigest Digest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

    private static FakeWorkspaceResolver FullWorkspaceResolver() =>
        new(new DataRetentionWorkspaceBinding(
            Guid.Parse("80808080-8080-4080-8080-808080808080"),
            "/invocation"));

    private sealed class FakePairReader(
        params HostProcessToolsMarkerPairJoinResult[] results)
        : IInstallationResetHostProcessToolsPairReader
    {

        private int _index;

        public int ReadCount { get; private set; }

        public Exception? Exception { get; set; }

        public Task<Result<HostProcessToolsMarkerPairJoinResult>> ReadAsync(
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            ReadCount++;

            if (Exception is { } exception)
            {

                throw exception;

            }

            HostProcessToolsMarkerPairJoinResult result =
                results[Math.Min(_index, results.Length - 1)];

            _index++;

            return Task.FromResult(
                Result<HostProcessToolsMarkerPairJoinResult>.Success(result));

        }

    }

    private sealed class FakeRemediationVerifier(
        FullInstallationResetRemediationAuthorization authorization)
        : IFullInstallationResetRemediationAttestationVerifier
    {

        public FullInstallationResetRemediationAuthorization Authorization { get; set; } =
            authorization;

        public Exception? Exception { get; set; }

        public int VerifyCount { get; private set; }

        public int MatchCount { get; private set; }

        public bool ClaimMatches { get; set; } = true;

        public Result<FullInstallationResetRemediationAuthorization> Verify(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair)
        {

            VerifyCount++;

            if (Exception is { } exception)
            {

                throw exception;

            }

            return Result<FullInstallationResetRemediationAuthorization>.Success(
                Authorization);

        }

        public Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid authenticatedInstallationId,
            HostProcessToolsMatchedPair persistedPair,
            DateTimeOffset acceptedAtUtc) =>
            Result<FullInstallationResetRemediationAuthorization>.Failure(
                new Error(
                    ErrorCodes.Data.ExternalRemediationInvalid,
                    "The test recovery verifier is intentionally inert until service recovery is implemented."));

        public bool MatchesAuthenticatedClaim(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair,
            Guid acceptedOperationId,
            Guid acceptedInstallationId,
            CovenantDigest acceptedAttestationDigest,
            CovenantDigest acceptedNonceDigest,
            CovenantDigest acceptedIssuerDigest)
        {

            MatchCount++;

            return ClaimMatches
                && acceptedOperationId == Authorization.OperationId
                && acceptedInstallationId == Authorization.InstallationId
                && acceptedAttestationDigest == Authorization.AttestationDigest
                && acceptedNonceDigest == Authorization.NonceDigest
                && acceptedIssuerDigest == Authorization.IssuerDigest;

        }

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

    private static InstallationResetFileSystemInventory InventoryWithTarget(
        string resourceId,
        string canonicalPath) =>
        new(
            Targets:
            [
                new InstallationResetTargetDescriptor(
                    "installation-file",
                    InstallationResetTargetRole.FileSystem,
                    resourceId,
                    canonicalPath,
                    DatabasePredicate: null,
                    Identity: null,
                    Rows: null,
                    Files: 0,
                    EstimatedBytes: 0),
            ],
            PreservedBackups: [],
            Exclusions: [],
            Files: 0,
            EstimatedBytes: 0);

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

    private sealed class FakeActiveStore :
        IInstallationResetActiveStore,
        IInstallationResetDatabaseIdentityReader
    {

        public FakeActiveStore()
        {

            string parent = Path.Combine(
                Path.GetTempPath(),
                $"arcanum-reset-service-{Guid.NewGuid():N}");

            RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions
                .CreateOwnerOnlyDirectoryAtPath(parent);

            GuardedRoot = Path.Combine(parent, "grimoire");

        }

        public string GuardedRoot { get; }

        public bool Written => Writes.Count > 0;

        public bool Retired { get; private set; }

        public int IdentityReadCount { get; private set; }

        public int RecoverCount { get; private set; }

        public InstallationResetActiveRecord? Record { get; private set; }

        public List<InstallationResetActiveRecord> Writes { get; } = [];

        public Func<InstallationResetActiveRecord, Result>? WriteOverride { get; set; }

        public List<Guid> RetiredOperationIds { get; } = [];

        public Result RetireResult { get; set; } = Result.Success();

        public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            RecoverCount++;

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(RecoveryState());

        }

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken = default) =>
            WriteAuthenticatedAsync(
                heldInstallationLock,
                record,
                cancellationToken);

        public Task<Result<InstallationResetActivePublication>> AdvanceAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            InstallationResetActiveRecord next,
            CancellationToken cancellationToken = default) =>
            WriteAuthenticatedAsync(
                heldInstallationLock,
                next,
                cancellationToken);

        public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(RecoveryState());

        }

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            CancellationToken cancellationToken = default) =>
            AuthenticatedSurfaceNotUsed<Result<InstallationResetActivePublication>>();

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord expectedRecord,
            FileHandleIdentity expectedIdentity,
            CancellationToken cancellationToken = default) =>
            AuthenticatedSurfaceNotUsed<Result<InstallationResetActivePublication>>();

        public Task<Result> RetireAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid operationId,
            CancellationToken cancellationToken = default)
        {

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            return RetireAsync(operationId, cancellationToken);

        }

        public Task<Result> CompleteStartupCleanupAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(Result.Success());

        }

        Task<Result<Guid>> IInstallationResetDatabaseIdentityReader.ReadAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            IdentityReadCount++;

            return Task.FromResult(Result<Guid>.Success(
                Guid.Parse("40404040-4040-4040-8040-404040404040")));

        }

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

        private async Task<Result<InstallationResetActivePublication>>
            WriteAuthenticatedAsync(
                ArcanumMaintenanceLock heldInstallationLock,
                InstallationResetActiveRecord record,
                CancellationToken cancellationToken)
        {

            heldInstallationLock.AssertHeldFor(GuardedRoot);

            Result written = await WriteAsync(record, cancellationToken)
                .ConfigureAwait(false);

            return written.IsSuccess
                ? Result<InstallationResetActivePublication>.Success(
                    Publication(record))
                : Result<InstallationResetActivePublication>.Failure(
                    written.Error);

        }

        private Result<InstallationResetActiveRecoveryState> RecoveryState() =>
            Result<InstallationResetActiveRecoveryState>.Success(
                Record is null
                    ? new InstallationResetActiveRecoveryState(
                        InstallationResetActiveRecoveryOutcome.NoActiveRecord,
                        Publication: null,
                        LegacyRecord: null)
                    : new InstallationResetActiveRecoveryState(
                        InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
                        Publication(Record),
                        LegacyRecord: null));

        private static InstallationResetActivePublication Publication(
            InstallationResetActiveRecord record) =>
            new(
                Location: null!,
                Envelope: null!,
                EnvelopeDigest: default,
                InstallationResetActivePayloadV2.FromRecord(record),
                Anchor: null!);

        private static Task<T> AuthenticatedSurfaceNotUsed<T>() =>
            throw new InvalidOperationException(
                "This legacy-only test double must not receive an authenticated-store call.");

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
