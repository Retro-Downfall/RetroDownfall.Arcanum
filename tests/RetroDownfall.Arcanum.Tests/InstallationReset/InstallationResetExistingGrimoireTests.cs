using System.Security.Cryptography;

using System.Text;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Workspaces;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using ArcanumMaintenanceLock =
    RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock;

using ArcanumMaintenanceLockAcquisitionResult =
    RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLockAcquisitionResult;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

[Collection("ProcessEnvironment")]
public sealed class InstallationResetExistingGrimoireTests : IDisposable
{

    private readonly GrimoireFixture _fixture;

    private readonly string _testHome = Path.Combine(
        Path.GetTempPath(),
        "arcanum-installation-reset",
        Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string?> _originalEnvironment = [];

    public InstallationResetExistingGrimoireTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);

    }

    public void Dispose()
    {

        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {

            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);

        }

        try
        {

            if (Directory.Exists(_testHome))
            {

                Directory.Delete(_testHome, recursive: true);

            }

        }
        catch
        {

            // Best-effort cleanup of an isolated test root.

        }

    }

    [Fact]
    public void Restricted_registration_resolves_without_host_data_services_or_state_creation()
    {

        using ServiceProvider provider = CreateProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetService service = scope.ServiceProvider
            .GetRequiredService<IInstallationResetService>();

        Assert.NotNull(service);

        Assert.False(Directory.Exists(_testHome));

    }

    [Fact]
    public async Task Missing_global_grimoire_reports_unavailable_without_creating_any_path()
    {

        using ServiceProvider provider = CreateProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetService service = scope.ServiceProvider
            .GetRequiredService<IInstallationResetService>();

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                _testHome),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.DataInventoryAvailable);

        Assert.Null(result.Value.Rows);

        Assert.False(Directory.Exists(_testHome));

    }

    [SkippableFact]
    public async Task Full_reset_marker_evidence_uses_only_the_narrow_six_column_projection()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        await using (ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile))
        {

            _ = await context.Database.ExecuteSqlRawAsync(
                "PRAGMA ignore_check_constraints = ON;");

            _ = await context.Database.ExecuteSqlRawAsync(
                """
                UPDATE covenant_authority_state
                SET AuthorityEpoch = 'unrelated-epoch',
                    CurrentMasterKeyVersion = 'unrelated-version',
                    CurrentMasterKeyFingerprint = 'unrelated-fingerprint',
                    RecoveryEnvelopeEpoch = 'unrelated-recovery';
                """);

            await context.Database.CloseConnectionAsync();

        }

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetHostProcessToolsDatabaseEvidenceReader reader =
            new StoppedHostEvidenceReader(
                scope.ServiceProvider.GetRequiredService<
                    IInstallationResetStoppedHostDataService>());

        Result<HostProcessToolsDatabaseMarkerEvidence> evidence =
            await reader.ReadMarkerEvidenceAsync(CancellationToken.None);

        Assert.True(evidence.IsSuccess, evidence.Error.Message);

        Assert.Equal(CovenantHostToolsState.Clean, evidence.Value.State);

        Assert.Null(evidence.Value.TransitionId);

        Assert.Null(evidence.Value.TaintMasterKeyVersion);

        Assert.Null(evidence.Value.TaintFingerprint);

    }

    [SkippableFact]
    public async Task Public_existing_global_grimoire_plan_is_conservative_and_creates_no_sqlite_sidecars()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Dictionary<string, FileSnapshot> before = CaptureFiles();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetService service = scope.ServiceProvider
            .GetRequiredService<IInstallationResetService>();

        Result<InstallationResetPlan> result = await service.PlanAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                _testHome),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.False(result.Value.DataInventoryAvailable);

        Assert.Null(result.Value.Rows);

        Assert.Equal(before, CaptureFiles());

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-wal"));

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile + "-shm"));

    }

    [SkippableFact]
    public async Task Workspace_plan_uses_most_specific_campaign_from_read_only_catalog()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        string parent = Path.Combine(_testHome, "workspace");

        string nested = Path.Combine(parent, "nested");

        Directory.CreateDirectory(Path.Combine(nested, "src"));

        Guid nestedId = await AddCampaignsAsync(parent, nested);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetWorkspaceResolver resolver =
            new StoppedHostWorkspaceResolver(
                scope.ServiceProvider.GetRequiredService<
                    IInstallationResetStoppedHostDataService>());

        Result<InstallationResetWorkspaceResolution> result =
            await resolver.ResolveAsync(
                Path.Combine(nested, "src"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(nestedId, result.Value.Workspace.CampaignId);

        Assert.Equal(Path.GetFullPath(nested), result.Value.Workspace.WorkspaceRoot);

    }

    [Fact]
    public async Task Missing_global_grimoire_lockless_apply_is_refused_without_creating_a_database()
    {

        using ServiceProvider provider = CreateProvider();

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetService service = scope.ServiceProvider
            .GetRequiredService<IInstallationResetService>();

        InstallationResetPlanRequest request = new(
            InstallationResetScope.Global,
            _testHome);

        Result<InstallationResetPlan> plan = await service.PlanAsync(
            request,
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        Result<InstallationResetResult> applied = await service.ApplyAsync(
            new InstallationResetApplyRequest(
                request,
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, applied.Error.Code);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        InstallationResetActiveStore activeStore = new(
            ArcanumPaths.GrimoireDirectory);

        Assert.False(File.Exists(activeStore.ActivePath));

        Assert.False(Directory.Exists(_testHome));

    }

    [SkippableFact]
    public async Task Workspace_apply_recovers_precommit_mutation_then_applies_accepted_plan()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Guid campaignId = Guid.NewGuid();

        string workspaceRoot = Path.GetFullPath(
            Path.Combine(_testHome, "workspace-recovery"));

        await AddWorkspaceDataAsync(campaignId, workspaceRoot);

        DataRetentionWorkspaceBinding binding = new(
            campaignId,
            workspaceRoot);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionPlan> plan = await dataService.PlanAsync(
            new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                binding),
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        Guid interruptedOperationId = await SeedInterruptedMutationAsync(
            "reset-workspace",
            campaignId.ToString("N") + ":" + workspaceRoot,
            state: LongRunningOperationState.ReconciliationRequired);

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: binding),
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(0, await CountWorkspaceContextsAsync(workspaceRoot));

        LongRunningOperation interrupted = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(interruptedOperationId));

        Assert.Equal(LongRunningOperationState.Failed, interrupted.State);

    }

    [SkippableFact]
    public async Task Workspace_apply_rejects_malformed_mutation_without_changing_it()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Guid campaignId = Guid.NewGuid();

        string workspaceRoot = Path.GetFullPath(
            Path.Combine(_testHome, "workspace-malformed"));

        await AddWorkspaceDataAsync(campaignId, workspaceRoot);

        DataRetentionWorkspaceBinding binding = new(
            campaignId,
            workspaceRoot);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionPlan> plan = await dataService.PlanAsync(
            new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                binding),
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        Guid interruptedOperationId = await SeedInterruptedMutationAsync(
            "reset-workspace",
            campaignId.ToString("N") + ":" + workspaceRoot,
            checkpointPayload: [0xFF]);

        LongRunningOperation before = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(interruptedOperationId));

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: binding),
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, applied.Error.Code);

        Assert.Equal(1, await CountWorkspaceContextsAsync(workspaceRoot));

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(interruptedOperationId));

        AssertOperationUnchanged(before, after);

    }

    [SkippableFact]
    public async Task Workspace_apply_returns_success_when_recovery_proves_commit()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Guid campaignId = Guid.NewGuid();

        string workspaceRoot = Path.GetFullPath(
            Path.Combine(_testHome, "workspace-committed"));

        await AddWorkspaceDataAsync(campaignId, workspaceRoot);

        DataRetentionWorkspaceBinding binding = new(
            campaignId,
            workspaceRoot);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionPlan> plan = await dataService.PlanAsync(
            new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                binding),
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        Guid interruptedOperationId = await SeedInterruptedMutationAsync(
            "reset-workspace",
            campaignId.ToString("N") + ":" + workspaceRoot);

        await DeleteWorkspaceContextsAsync(workspaceRoot);

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: binding),
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(interruptedOperationId, applied.Value.OperationId);

        Assert.True(applied.Value.Reconciled);

        LongRunningOperation interrupted = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(interruptedOperationId));

        Assert.Equal(LongRunningOperationState.Completed, interrupted.State);

    }

    [SkippableTheory]
    [InlineData("reset-memory", false)]
    [InlineData("reset-workspace", true)]
    public async Task Workspace_apply_rejects_nonmatching_mutation_without_changing_it(
        string subtype,
        bool useMismatchedWorkspaceTarget)
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Guid campaignId = Guid.NewGuid();

        string workspaceRoot = Path.GetFullPath(
            Path.Combine(_testHome, "workspace-mismatch"));

        await AddWorkspaceDataAsync(campaignId, workspaceRoot);

        DataRetentionWorkspaceBinding binding = new(
            campaignId,
            workspaceRoot);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionPlan> plan = await dataService.PlanAsync(
            new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                binding),
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        string checkpointTarget = useMismatchedWorkspaceTarget
            ? Guid.NewGuid().ToString("N") + ":" + workspaceRoot
            : "1";

        Guid interruptedOperationId = await SeedInterruptedMutationAsync(
            subtype,
            checkpointTarget);

        LongRunningOperation before = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(interruptedOperationId));

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: binding),
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, applied.Error.Code);

        Assert.Equal(1, await CountWorkspaceContextsAsync(workspaceRoot));

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(interruptedOperationId));

        AssertOperationUnchanged(before, after);

    }

    [SkippableFact]
    public async Task Workspace_apply_rejects_ambiguous_mutations_without_changing_them()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Guid campaignId = Guid.NewGuid();

        string workspaceRoot = Path.GetFullPath(
            Path.Combine(_testHome, "workspace-ambiguous"));

        await AddWorkspaceDataAsync(campaignId, workspaceRoot);

        DataRetentionWorkspaceBinding binding = new(
            campaignId,
            workspaceRoot);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionPlan> plan = await dataService.PlanAsync(
            new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                binding),
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        string checkpointTarget = campaignId.ToString("N")
            + ":"
            + workspaceRoot;

        Guid firstId = await InsertInterruptedMutationAsync(checkpointTarget);

        Guid secondId = await InsertInterruptedMutationAsync(checkpointTarget);

        LongRunningOperation firstBefore = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(firstId));

        LongRunningOperation secondBefore = Assert.IsType<LongRunningOperation>(
            await ReadOperationAsync(secondId));

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: binding),
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsFailure);

        Assert.Equal(ErrorCodes.Data.RecoveryRequired, applied.Error.Code);

        Assert.Equal(1, await CountWorkspaceContextsAsync(workspaceRoot));

        AssertOperationUnchanged(
            firstBefore,
            Assert.IsType<LongRunningOperation>(await ReadOperationAsync(firstId)));

        AssertOperationUnchanged(
            secondBefore,
            Assert.IsType<LongRunningOperation>(await ReadOperationAsync(secondId)));

    }

    [SkippableFact]
    public async Task Global_apply_recognizes_the_exact_completed_factory_reset_plan()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionPlan> plan = await dataService.PlanAsync(
            new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        Guid completedOperationId = await InsertCompletedFactoryResetAsync(
            plan.Value.PlanId);

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(completedOperationId, applied.Value.OperationId);

        Assert.True(applied.Value.Reconciled);

    }

    [SkippableFact]
    public async Task Workspace_apply_recognizes_the_exact_completed_workspace_reset_plan()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Guid campaignId = Guid.NewGuid();

        string workspaceRoot = Path.GetFullPath(
            Path.Combine(_testHome, "completed-workspace-reset"));

        await AddWorkspaceDataAsync(campaignId, workspaceRoot);

        DataRetentionWorkspaceBinding binding = new(campaignId, workspaceRoot);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionPlan> plan = await dataService.PlanAsync(
            new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                binding),
            CancellationToken.None);

        Assert.True(plan.IsSuccess, plan.Error.Message);

        Guid completedOperationId = await InsertCompletedWorkspaceResetAsync(
            binding,
            plan.Value.PlanId);

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(
                    DataRetentionOperation.ResetWorkspace,
                    Workspace: binding),
                plan.Value.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(completedOperationId, applied.Value.OperationId);

        Assert.True(applied.Value.Reconciled);

    }

    private ServiceProvider CreateProvider()
    {

        ServiceCollection services = new();

        services.AddLogging();

        services.AddArcanumCliClientStack();

        services.AddSingleton<IInstallationResetPreDataMutation>(
            NoopInstallationResetPreDataMutation.Instance);

        services.AddArcanumInstallationReset(new ArcanumSettings());

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

    }

    private static IInstallationResetDataService StoppedHostDataService(
        IServiceScope scope) =>
        new StoppedHostDataServiceAdapter(
            scope.ServiceProvider.GetRequiredService<
                IInstallationResetStoppedHostDataService>());

    private static async Task<Result<T>> UnderStoppedHostAuthorityAsync<T>(
        Func<
            IStoppedHostGrimoireAuthorityIssuer,
            CancellationToken,
            Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(
                ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock heldLock = acquired.BorrowAcquiredLock();

        IStoppedHostGrimoireAuthorityIssuer issuer =
            new StoppedHostGrimoireAuthorityIssuer(
                heldLock,
                ArcanumPaths.GrimoireDirectory,
                ArcanumPaths.GrimoireDatabaseFile);

        return await operation(issuer, cancellationToken).ConfigureAwait(false);

    }

    private sealed class StoppedHostDataServiceAdapter(
        IInstallationResetStoppedHostDataService inner)
        : IInstallationResetDataService
    {

        public Task<Result<DataRetentionPlan>> PlanAsync(
            InstallationResetDataPlanRequest request,
            CancellationToken cancellationToken = default) =>
            UnderStoppedHostAuthorityAsync(
                (issuer, token) => inner.PlanUnderStoppedHostAuthorityAsync(
                    request,
                    issuer,
                    token),
                cancellationToken);

        public Task<Result<DataRetentionApplyResult>> ApplyAsync(
            DataRetentionApplyRequest request,
            CancellationToken cancellationToken = default) =>
            UnderStoppedHostAuthorityAsync(
                (issuer, token) => inner.ApplyUnderStoppedHostAuthorityAsync(
                    request,
                    issuer,
                    token),
                cancellationToken);

    }

    private sealed class StoppedHostWorkspaceResolver(
        IInstallationResetStoppedHostDataService inner)
        : IInstallationResetWorkspaceResolver
    {

        public Task<Result<InstallationResetWorkspaceResolution>> ResolveAsync(
            string invocationDirectory,
            CancellationToken cancellationToken) =>
            UnderStoppedHostAuthorityAsync(
                (issuer, token) =>
                    inner.ResolveWorkspaceUnderStoppedHostAuthorityAsync(
                        invocationDirectory,
                        issuer,
                        token),
                cancellationToken);

    }

    private sealed class StoppedHostEvidenceReader(
        IInstallationResetStoppedHostDataService inner)
        : IInstallationResetHostProcessToolsDatabaseEvidenceReader
    {

        public Task<Result<HostProcessToolsDatabaseMarkerEvidence>>
            ReadMarkerEvidenceAsync(CancellationToken cancellationToken) =>
            UnderStoppedHostAuthorityAsync(
                static (issuer, token, state) =>
                    state.ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(
                        issuer,
                        token),
                cancellationToken,
                inner);

    }

    private static Task<Result<T>> UnderStoppedHostAuthorityAsync<T, TState>(
        Func<
            IStoppedHostGrimoireAuthorityIssuer,
            CancellationToken,
            TState,
            Task<Result<T>>> operation,
        CancellationToken cancellationToken,
        TState state) =>
        UnderStoppedHostAuthorityAsync(
            (issuer, token) => operation(issuer, token, state),
            cancellationToken);

    private async Task InstallExistingGrimoireAsync(ServiceProvider provider)
    {

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string fixtureCopy = _fixture.CopyDatabase();

        File.Copy(
            fixtureCopy,
            ArcanumPaths.GrimoireDatabaseFile,
            overwrite: true);

        File.Copy(
            fixtureCopy + ".kdf",
            GrimoireKdfSidecarFile.GetSidecarPath(
                ArcanumPaths.GrimoireDatabaseFile),
            overwrite: true);

        DataProtectionSecretStore secrets = provider
            .GetRequiredService<DataProtectionSecretStore>();

        await secrets.SaveGrimoireEncryptionSecretAsync(
            GrimoireFixture.TestGrimoireSecret);

    }

    private async Task<Guid> AddCampaignsAsync(string parent, string nested)
    {

        Guid parentId = Guid.NewGuid();

        Guid nestedId = Guid.NewGuid();

        await using (ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile))
        {

            DateTimeOffset now = DateTimeOffset.UtcNow;

            context.Campaigns.AddRange(
                CreateCampaign(parentId, "Parent", parent, now),
                CreateCampaign(nestedId, "Nested", nested, now));

            await context.SaveChangesAsync();

            await context.Database.CloseConnectionAsync();

        }

        File.Delete(ArcanumPaths.GrimoireDatabaseFile + "-wal");

        File.Delete(ArcanumPaths.GrimoireDatabaseFile + "-shm");

        return nestedId;

    }

    /// <summary>
    /// A labelled Saga memory refuses the untargeted memory reset under stopped-host authority, as it
    /// does through the serving host.
    /// </summary>
    /// <remarks>
    /// The stopped-host reset composes its own retention service by hand - there is no container,
    /// because the host is stopped - so the labelled-artifact guard it is given has to work over a
    /// connection the ordinary admission factory never opened. Wiring the guard is not enough on its
    /// own: a connection source that asks that factory to admit an already-open maintenance lease is
    /// refused, and the refusal surfaces as a maintenance-unavailable throw rather than the
    /// Covenant refusal the caller is owed. This asserts the outcome the container-hosted route
    /// returns, so the two paths cannot answer a labelled artifact differently.
    /// </remarks>
    [SkippableFact]
    public async Task Labeled_saga_memory_refuses_the_untargeted_reset_under_stopped_host_authority()
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using ServiceProvider provider = CreateProvider();

        await InstallExistingGrimoireAsync(provider);

        Guid memoryId = Guid.NewGuid();

        await SeedLabeledSagaMemoryAsync(memoryId);

        using IServiceScope scope = provider.CreateScope();

        IInstallationResetDataService dataService = StoppedHostDataService(scope);

        Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
            new DataRetentionApplyRequest(
                new DataRetentionRequest(
                    DataRetentionOperation.ResetMemory,
                    MemoryScope: MemoryResetScope.Saga)),
            CancellationToken.None);

        Assert.True(applied.IsFailure, "The labelled Saga memory was reset under stopped-host authority.");

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, applied.Error.Code);

        // The refusal is only worth anything if it stopped the delete, and the label has to outlive it
        // too: a row removed from under a live label is the one integrity state that cannot be told
        // apart from data loss.
        Assert.Equal(1, await CountRowsAsync("saga_memories", "Id", memoryId));

        Assert.Equal(1, await CountRowsAsync("artifact_sensitivity", "ArtifactId", memoryId));

    }

    private async Task SeedLabeledSagaMemoryAsync(Guid memoryId)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        await using (SqliteCommand memory = connection.CreateCommand())
        {

            memory.CommandText = """
                INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
                VALUES ($id, 'labelled saga fact', $created, 1);
                """;

            _ = memory.Parameters.AddWithValue("$id", memoryId.ToString("D").ToUpperInvariant());

            _ = memory.Parameters.AddWithValue("$created", "2026-01-01T00:00:00.0000000Z");

            _ = await memory.ExecuteNonQueryAsync(CancellationToken.None);

        }

        // Written through the ledger the serving host writes labels with, so the row this test
        // protects is shaped by production rather than by the test's idea of a label.
        CovenantConnectionSource connections = new(
            context,
            FixtureOrdinaryConnectionFactory.For(context));

        try
        {

            ArtifactSensitivityLedger ledger = new(connections);

            Result<LabeledArtifactWriteReceipt> receipt = await ledger.LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.Saga,
                    memoryId,
                    null,
                    null,
                    null,
                    1,
                    CovenantTask6Fixture.D(11),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.Parse("5E6F7081-92A3-4B5C-8D9E-0F1A2B3C4D5E")])),
                CancellationToken.None);

            Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.Message : string.Empty);

        }
        finally
        {

            connections.Dispose();

        }

    }

    private async Task<int> CountRowsAsync(string table, string column, Guid id)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();

        if (connection.State is not System.Data.ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $id;";

        _ = command.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            global::System.Globalization.CultureInfo.InvariantCulture);

    }

    private async Task AddWorkspaceDataAsync(Guid campaignId, string workspaceRoot)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        context.Campaigns.Add(
            CreateCampaign(
                campaignId,
                "Recovery",
                workspaceRoot,
                now));

        context.WorkspaceContexts.Add(new WorkspaceContext
        {
            Id = Guid.NewGuid(),
            WorkspacePath = workspaceRoot,
            SerializedSnapshot = "{}",
            CreatedAt = now,
        });

        await context.SaveChangesAsync();

    }

    private async Task<Guid> SeedInterruptedMutationAsync(
        string subtype,
        string target,
        byte[]? checkpointPayload = null,
        LongRunningOperationState state = LongRunningOperationState.Running)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        LongRunningOperationStore operations = new(
            context,
            TestOrdinaryConnectionFactory.For(context));

        DateTimeOffset now = DateTimeOffset.UtcNow;

        const string ownerId = "interrupted-installation-reset-test";

        LongRunningOperation operation = Assert.IsType<LongRunningOperation>(
            await operations.TryStartSingleFlightAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionMutation,
                    LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                    "Interrupted canonical workspace reset.",
                    now),
                ownerId,
                now,
                now.AddMinutes(5),
                CancellationToken.None));

        bool saved = await operations.SaveCheckpointAsync(
            operation.Id,
            ownerId,
            expectedCheckpointVersion: 0,
            checkpointVersion: 2,
            checkpointPayload ?? BuildMutationJournal(subtype, target),
            "retention-mutation:" + operation.Id.ToString("N"),
            "Durable retention mutation request is ready.",
            now,
            CancellationToken.None);

        Assert.True(saved);

        if (state is LongRunningOperationState.ReconciliationRequired)
        {

            LongRunningOperation latest = Assert.IsType<LongRunningOperation>(
                await operations.GetAsync(
                    operation.Id,
                    CancellationToken.None));

            bool transitioned = await operations.TryTransitionAsync(
                latest.Id,
                latest.Revision,
                ownerId,
                LongRunningOperationState.ReconciliationRequired,
                now,
                ErrorCodes.Data.ReconciliationFailed,
                CancellationToken.None);

            Assert.True(transitioned);

        }

        return operation.Id;

    }

    private async Task<LongRunningOperation?> ReadOperationAsync(Guid operationId)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        return await new LongRunningOperationStore(
            context,
            TestOrdinaryConnectionFactory.For(context)).GetAsync(
            operationId,
            CancellationToken.None);

    }

    private async Task<Guid> InsertInterruptedMutationAsync(string target)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        Guid operationId = Guid.NewGuid();

        string now = DateTimeOffset.UtcNow.ToString("O");

        byte[] payload = BuildMutationJournal("reset-workspace", target);

        string checkpointReference = "retention-mutation:"
            + operationId.ToString("N");

        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "LongRunningOperations"
                ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "StartedAt",
                 "HeartbeatAt", "LeaseOwner", "LeaseExpiresAt", "AttemptCount",
                 "CheckpointVersion", "CheckpointPayload", "CheckpointReference",
                 "PublicSummary", "Revision")
            VALUES
                ({operationId.ToString("N")}, {LongRunningOperationKinds.DataRetentionMutation},
                 {(int)LongRunningOperationState.Running},
                 {(int)LongRunningOperationRecoveryPolicy.ReconcileAndComplete},
                 {now}, {now}, {now}, {"interrupted-installation-reset-test"}, {now}, 1,
                 2, {payload}, {checkpointReference},
                 {"Durable retention mutation request is ready."}, 2)
            """);

        return operationId;

    }

    private async Task<Guid> InsertCompletedFactoryResetAsync(string planId)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        Guid operationId = Guid.NewGuid();

        string now = DateTimeOffset.UtcNow.ToString("O");

        string summary = $"Applying FactoryReset data-retention plan {planId}.";

        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "LongRunningOperations"
                ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "StartedAt",
                 "HeartbeatAt", "CompletedAt", "AttemptCount", "CheckpointVersion",
                 "PublicSummary", "Revision")
            VALUES
                ({operationId.ToString("N")}, {LongRunningOperationKinds.DataRetentionFactoryReset},
                 {(int)LongRunningOperationState.Completed},
                 {(int)LongRunningOperationRecoveryPolicy.RestartIdempotently},
                 {now}, {now}, {now}, {now}, 1, 0, {summary}, 2)
            """);

        return operationId;

    }

    private async Task<Guid> InsertCompletedWorkspaceResetAsync(
        DataRetentionWorkspaceBinding binding,
        string planId)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        Guid operationId = Guid.NewGuid();

        string now = DateTimeOffset.UtcNow.ToString("O");

        string target = binding.CampaignId.ToString("N")
            + ":"
            + binding.WorkspaceRoot;

        byte[] payload = BuildMutationJournal("reset-workspace", target);

        string checkpointReference = "retention-mutation:"
            + operationId.ToString("N");

        string summary = $"Applying ResetWorkspace data-retention plan {planId}.";

        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "LongRunningOperations"
                ("Id", "Kind", "State", "RecoveryPolicy", "CreatedAt", "StartedAt",
                 "HeartbeatAt", "CompletedAt", "AttemptCount", "CheckpointVersion",
                 "CheckpointPayload", "CheckpointReference", "PublicSummary", "Revision")
            VALUES
                ({operationId.ToString("N")}, {LongRunningOperationKinds.DataRetentionMutation},
                 {(int)LongRunningOperationState.Completed},
                 {(int)LongRunningOperationRecoveryPolicy.ReconcileAndComplete},
                 {now}, {now}, {now}, {now}, 1, 2, {payload}, {checkpointReference},
                 {summary}, 2)
            """);

        return operationId;

    }

    private async Task<int> CountWorkspaceContextsAsync(string workspaceRoot)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        return await context.WorkspaceContexts
            .AsNoTracking()
            .CountAsync(item => item.WorkspacePath == workspaceRoot);

    }

    private async Task DeleteWorkspaceContextsAsync(string workspaceRoot)
    {

        await using ArcanumDbContext context = _fixture.CreateContext(
            ArcanumPaths.GrimoireDatabaseFile);

        _ = await context.WorkspaceContexts
            .Where(item => item.WorkspacePath == workspaceRoot)
            .ExecuteDeleteAsync();

    }

    private static void AssertOperationUnchanged(
        LongRunningOperation before,
        LongRunningOperation after)
    {

        Assert.Equal(before.State, after.State);

        Assert.Equal(before.Revision, after.Revision);

        Assert.Equal(before.LeaseOwner, after.LeaseOwner);

        Assert.Equal(before.LeaseExpiresAt, after.LeaseExpiresAt);

        Assert.Equal(before.AttemptCount, after.AttemptCount);

        Assert.Equal(before.CheckpointPayload, after.CheckpointPayload);

    }

    private static byte[] BuildMutationJournal(string subtype, string target)
    {

        string body = "ARCAMUT2\n"
            + subtype
            + "\n"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(target))
            + "\n0\n";

        string digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        return Encoding.UTF8.GetBytes(body + "H:" + digest + "\n");

    }

    private static Campaign CreateCampaign(
        Guid id,
        string name,
        string path,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            Name = name,
            NameLower = name.ToLowerInvariant(),
            Path = Path.GetFullPath(path),
            Type = WorkspaceType.Campaign,
            Settings = "{}",
            SanctumConfigJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };

    private Dictionary<string, FileSnapshot> CaptureFiles() =>
        Directory.Exists(_testHome)
            ? Directory.GetFiles(_testHome, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToDictionary(
                    static path => path,
                    static path => new FileSnapshot(
                        File.GetLastWriteTimeUtc(path),
                        new FileInfo(path).Length,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))),
                    StringComparer.Ordinal)
            : [];

    private void SetEnvironment(string name, string value)
    {

        _originalEnvironment[name] =
            global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);

    }

    private sealed record FileSnapshot(
        DateTime LastWriteTimeUtc,
        long Length,
        string Sha256);

}
