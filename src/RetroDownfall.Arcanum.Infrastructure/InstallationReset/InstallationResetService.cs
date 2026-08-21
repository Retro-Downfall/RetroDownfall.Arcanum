using System.Collections.Concurrent;

using System.Globalization;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IInstallationResetCredentialService
{

    InstallationResetCredentialSummary[] Probe();

    InstallationResetCredentialResult[] DeleteAndVerify(string[] accounts);

}

internal interface IInstallationResetWorkspaceResolver
{

    Task<Result<InstallationResetWorkspaceResolution>> ResolveAsync(
        string invocationDirectory,
        CancellationToken cancellationToken);

}

internal sealed record InstallationResetWorkspaceResolution(
    DataRetentionWorkspaceBinding Workspace,
    string[] ExcludedRoots);

internal sealed record InstallationResetOfflineCleanupResult(
    long FilesDeleted,
    long EstimatedBytesDeleted,
    InstallationResetCredentialResult[] CredentialResults,
    InstallationResetPreservedBackup[] PreservedBackups,
    InstallationResetVerification Verification);

internal interface IInstallationResetOfflineCleanup
{

    Task<Result<InstallationResetFileSystemInventory>> PlanAsync(
        string[] selectedRoots,
        string[] excludedRoots,
        CancellationToken cancellationToken);

    Task<Result<InstallationResetOfflineCleanupResult>> ExecuteAsync(
        InstallationResetPlan plan,
        CancellationToken cancellationToken);

}

internal interface IInstallationResetStateRoots
{

    string[] Resolve(
        InstallationResetScope scope,
        DataRetentionWorkspaceBinding? workspace);

}

internal interface IInstallationResetPreDataMutation
{

    Task<Result> ExecuteAsync(CancellationToken cancellationToken);

}

internal sealed class InstallationResetControlPaths
{

    public InstallationResetControlPaths(string guardedRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        LockPath = Backup.ArcanumMaintenanceLock.LockPathFor(guardedRoot);

        ActivePath = new InstallationResetActiveStore(guardedRoot).ActivePath;

    }

    public string LockPath { get; }

    public string ActivePath { get; }

}

internal sealed class NoopInstallationResetPreDataMutation : IInstallationResetPreDataMutation
{

    public static NoopInstallationResetPreDataMutation Instance { get; } = new();

    public Task<Result> ExecuteAsync(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Result.Success());

    }

}

internal sealed class InstallationResetStateRoots : IInstallationResetStateRoots
{

    public static InstallationResetStateRoots Default { get; } = new();

    public string[] Resolve(
        InstallationResetScope scope,
        DataRetentionWorkspaceBinding? workspace)
    {

        List<string> roots = [];

        if (scope is InstallationResetScope.Global or InstallationResetScope.All)
        {

            roots.Add(Path.GetFullPath(Core.Storage.ArcanumPaths.GrimoireDirectory));

            roots.Add(Path.GetFullPath(Core.Storage.ArcanumPaths.SecretStoreDirectory));

        }

        if (scope is InstallationResetScope.Workspace or InstallationResetScope.All)
        {

            if (workspace is null)
            {

                return [];

            }

            roots.Add(Path.GetFullPath(Path.Combine(
                workspace.WorkspaceRoot,
                ".arcanum")));

        }

        return [
            .. roots
                .Select(Path.TrimEndingDirectorySeparator)
                .Distinct(PathComparer)
                .Order(PathComparer),
        ];

    }

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

}

internal sealed class InstallationResetService(
    IInstallationResetDataService dataService,
    IInstallationResetCredentialService credentialService,
    IInstallationResetActiveStore activeStore,
    IInstallationResetOfflineCleanup offlineCleanup,
    TimeProvider? timeProvider = null,
    IInstallationResetWorkspaceResolver? workspaceResolver = null,
    IInstallationResetStateRoots? stateRoots = null,
    IInstallationResetPreDataMutation? preDataMutation = null,
    InstallationResetControlPaths? controlPaths = null)
    : IInstallationResetService, IInstallationResetOnlineDataHandoff
{

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private readonly IInstallationResetStateRoots _stateRoots =
        stateRoots ?? InstallationResetStateRoots.Default;

    private readonly IInstallationResetPreDataMutation _preDataMutation =
        preDataMutation ?? NoopInstallationResetPreDataMutation.Instance;

    private readonly ConcurrentDictionary<string, DataRetentionPlan> _localDataPlans =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DataRetentionPlan> _onlineDataPlans =
        new(StringComparer.Ordinal);

    public async Task<Result<InstallationResetPlan>> PlanAsync(
        InstallationResetPlanRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        DataRetentionWorkspaceBinding? workspace = null;

        string[] workspaceExclusions = [];

        if (request.Scope is InstallationResetScope.Workspace or InstallationResetScope.All)
        {

            if (workspaceResolver is null)
            {

                return Result<InstallationResetPlan>.Failure(new Error(
                    ErrorCodes.Data.InventoryUnavailable,
                    "The registered Campaign inventory is unavailable."));

            }

            Result<InstallationResetWorkspaceResolution> resolved = await workspaceResolver
                .ResolveAsync(request.InvocationDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (resolved.IsFailure)
            {

                return Result<InstallationResetPlan>.Failure(resolved.Error);

            }

            workspace = resolved.Value.Workspace;

            workspaceExclusions = resolved.Value.ExcludedRoots;

        }

        InstallationResetDataPlanRequest dataRequest = request.Scope switch
        {
            InstallationResetScope.Global or InstallationResetScope.All =>
                new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
            _ => new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                workspace),
        };

        Result<DataRetentionPlan> dataPlan = await dataService
            .PlanAsync(dataRequest, cancellationToken)
            .ConfigureAwait(false);

        bool dataInventoryAvailable = dataPlan.IsSuccess;

        DataRetentionPlan? canonical = dataInventoryAvailable
            ? dataPlan.Value
            : null;

        if (dataPlan.IsFailure
            && (request.Scope is InstallationResetScope.Workspace
                || dataPlan.Error.Code != ErrorCodes.Data.InventoryUnavailable))
        {

            return Result<InstallationResetPlan>.Failure(dataPlan.Error);

        }

        InstallationResetCredentialSummary[] credentials =
            request.Scope is InstallationResetScope.Workspace
                ? []
                : credentialService.Probe();

        string[] accounts =
            [.. credentials.Select(static item => item.Account).Order(StringComparer.Ordinal)];

        string[] selectedRoots = _stateRoots.Resolve(request.Scope, workspace);

        if (workspace is not null
            && controlPaths is not null
            && (PathsOverlap(workspace.WorkspaceRoot, controlPaths.LockPath)
                || PathsOverlap(workspace.WorkspaceRoot, controlPaths.ActivePath)
                || selectedRoots.Any(root =>
                    PathsOverlap(root, controlPaths.LockPath)
                    || PathsOverlap(root, controlPaths.ActivePath))))
        {

            return Result<InstallationResetPlan>.Failure(new Error(
                ErrorCodes.Data.WorkspaceOverlap,
                "The selected workspace overlaps the installation reset control paths."));

        }

        string[] excludedRoots = workspaceExclusions;

        Result<InstallationResetFileSystemInventory> fileInventoryResult =
            await offlineCleanup.PlanAsync(
                selectedRoots,
                excludedRoots,
                cancellationToken).ConfigureAwait(false);

        if (fileInventoryResult.IsFailure)
        {

            return Result<InstallationResetPlan>.Failure(fileInventoryResult.Error);

        }

        InstallationResetFileSystemInventory fileInventory = fileInventoryResult.Value;

        excludedRoots =
        [
            .. workspaceExclusions
                .Concat(fileInventory.Exclusions.Select(
                    static exclusion => exclusion.ResourceId))
                .Distinct(PathComparer)
                .Order(PathComparer),
        ];

        InstallationResetAcceptedBinding provisional = new(
            BindingId: string.Empty,
            SelectedRoots: selectedRoots,
            ExcludedRoots: excludedRoots,
            PreservedBackups: fileInventory.PreservedBackups,
            CredentialAccounts: accounts,
            DataPlanIds: canonical is null ? [] : [canonical.PlanId]);

        string bindingId = ComputeBindingId(request, provisional);

        InstallationResetAcceptedBinding accepted = provisional with
        {
            BindingId = bindingId,
        };

        InstallationResetIssueSummary[] blockers =
        [
            .. (canonical?.Blockers ?? []).Select(static blocker =>
                new InstallationResetIssueSummary(
                    blocker.ReasonCode,
                    blocker.Message,
                    blocker.ResourceId)),
            .. credentials
                .Where(static credential =>
                    credential.Status is InstallationResetItemStatus.Unavailable)
                .Select(static credential => new InstallationResetIssueSummary(
                    ErrorCodes.Data.CredentialInventoryUnavailable,
                    "An accepted credential could not be inspected.",
                    credential.Account)),
        ];

        InstallationResetTargetDescriptor[] targets =
            [
                .. (canonical?.Items ?? []).Select(item =>
                new InstallationResetTargetDescriptor(
                    item.DataClass.ToString(),
                    InstallationResetTargetRole.Database,
                    item.DataClass.ToString(),
                    CanonicalPath: null,
                    DatabasePredicate: "canonical-data-plan:" + canonical!.PlanId,
                    Identity: null,
                    Rows: item.Rows,
                    Files: item.Files,
                    EstimatedBytes: item.EstimatedBytes)),
                .. fileInventory.Targets,
                .. request.Scope is InstallationResetScope.Global or InstallationResetScope.All
                    ? new InstallationResetTargetDescriptor[]
                    {
                        new InstallationResetTargetDescriptor(
                            "daemon-registration",
                            InstallationResetTargetRole.Daemon,
                            "platform-daemon-registration",
                            CanonicalPath: null,
                            DatabasePredicate: "platform-daemon-registration",
                            Identity: null,
                            Rows: null,
                            Files: 0,
                            EstimatedBytes: 0),
                    }
                    : Array.Empty<InstallationResetTargetDescriptor>(),
            ];

        string planId = ComputePlanId(
            request,
            accepted,
            canonical,
            credentials,
            fileInventory.Targets);

        InstallationResetPlan plan = new(
            planId,
            request.Scope,
            workspace,
            _timeProvider.GetUtcNow(),
            dataInventoryAvailable,
            CredentialInventoryAvailable: credentials.All(
                static item => item.Status is not InstallationResetItemStatus.Unavailable),
            targets,
            credentials,
            PreservedBackups: fileInventory.PreservedBackups,
            Exclusions: fileInventory.Exclusions,
            blockers,
            canonical?.Rows,
            checked((canonical?.Files ?? 0) + fileInventory.Files),
            checked((canonical?.EstimatedBytes ?? 0) + fileInventory.EstimatedBytes),
            accepted);

        if (canonical is not null)
        {

            _localDataPlans[plan.PlanId] = canonical;

        }

        return Result<InstallationResetPlan>.Success(plan);

    }

    public Result<InstallationResetPlan> BindOnlineDataPlan(
        InstallationResetPlanRequest request,
        InstallationResetPlan localPlan,
        DataRetentionPlan onlinePlan)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(localPlan);

        ArgumentNullException.ThrowIfNull(onlinePlan);

        if (request.Scope is InstallationResetScope.Workspace
            || localPlan.Scope != request.Scope
            || localPlan.AcceptedBinding.DataPlanIds.Length != 1
            || string.IsNullOrWhiteSpace(onlinePlan.PlanId)
            || !_localDataPlans.TryGetValue(localPlan.PlanId, out DataRetentionPlan? localData)
            || !SameOrdinaryDataPlan(localData, onlinePlan))
        {

            return PlanChanged<InstallationResetPlan>();

        }

        InstallationResetAcceptedBinding provisional = localPlan.AcceptedBinding with
        {
            BindingId = string.Empty,
            DataPlanIds = [onlinePlan.PlanId],
        };

        InstallationResetAcceptedBinding reboundBinding = provisional with
        {
            BindingId = ComputeBindingId(request, provisional),
        };

        InstallationResetTargetDescriptor[] reboundTargets =
        [
            .. localPlan.Targets.Select(target =>
                target.Role is InstallationResetTargetRole.Database
                    ? target with
                    {
                        DatabasePredicate = "canonical-data-plan:" + onlinePlan.PlanId,
                    }
                    : target),
        ];

        string planId = ComputePlanId(
            request,
            reboundBinding,
            onlinePlan,
            localPlan.Credentials,
            [.. reboundTargets.Where(static target =>
                target.Role is InstallationResetTargetRole.FileSystem)]);

        InstallationResetPlan rebound = localPlan with
        {
            PlanId = planId,
            Targets = reboundTargets,
            AcceptedBinding = reboundBinding,
        };

        _onlineDataPlans[rebound.PlanId] = onlinePlan;

        return Result<InstallationResetPlan>.Success(rebound);

    }

    public async Task<Result<InstallationResetOnlineDataHandoff>> PrepareAsync(
        InstallationResetApplyRequest request,
        InstallationResetPlan confirmedPlan,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(confirmedPlan);

        if (request.Request.Scope is InstallationResetScope.Workspace
            || confirmedPlan.Scope != request.Request.Scope
            || !string.Equals(
                request.ExpectedPlanId,
                confirmedPlan.PlanId,
                StringComparison.Ordinal))
        {

            return PlanChanged<InstallationResetOnlineDataHandoff>();

        }

        Result<InstallationResetActiveRecord?> activeRead = await activeStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            return Result<InstallationResetOnlineDataHandoff>.Failure(activeRead.Error);

        }

        if (activeRead.Value is { } existing)
        {

            Result validation = await ValidateResumeAsync(
                existing,
                request,
                cancellationToken).ConfigureAwait(false);

            if (validation.IsFailure)
            {

                return Result<InstallationResetOnlineDataHandoff>.Failure(validation.Error);

            }

            Result<InstallationResetOnlineDataHandoff> replay = ReadPreparedHandoff(
                existing,
                request);

            return replay;

        }

        if (!_onlineDataPlans.TryGetValue(
                confirmedPlan.PlanId,
                out DataRetentionPlan? onlinePlan))
        {

            return PlanChanged<InstallationResetOnlineDataHandoff>();

        }

        Result<InstallationResetPlan> replanned = await PlanAsync(
            request.Request,
            cancellationToken).ConfigureAwait(false);

        if (replanned.IsFailure)
        {

            return Result<InstallationResetOnlineDataHandoff>.Failure(replanned.Error);

        }

        Result<InstallationResetPlan> rebound = BindOnlineDataPlan(
            request.Request,
            replanned.Value,
            onlinePlan);

        if (rebound.IsFailure
            || !SameAcceptedInstallationPlan(rebound.Value, confirmedPlan))
        {

            return PlanChanged<InstallationResetOnlineDataHandoff>();

        }

        if (!confirmedPlan.CredentialInventoryAvailable)
        {

            return Result<InstallationResetOnlineDataHandoff>.Failure(new Error(
                ErrorCodes.Data.CredentialInventoryUnavailable,
                "The accepted credential inventory is unavailable."));

        }

        if (confirmedPlan.Blockers.Length > 0)
        {

            return Result<InstallationResetOnlineDataHandoff>.Failure(new Error(
                ErrorCodes.Data.Blocked,
                confirmedPlan.Blockers[0].Message));

        }

        Guid requestedOperationId = Guid.NewGuid();

        InstallationResetActiveRecord prepared = new(
            InstallationResetActiveStore.CurrentVersion,
            requestedOperationId,
            confirmedPlan.PlanId,
            confirmedPlan.Scope,
            confirmedPlan.Workspace,
            confirmedPlan.AcceptedBinding,
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure);

        Result published = await activeStore.WriteAsync(
            prepared,
            cancellationToken).ConfigureAwait(false);

        if (published.IsFailure)
        {

            return Result<InstallationResetOnlineDataHandoff>.Failure(published.Error);

        }

        return Result<InstallationResetOnlineDataHandoff>.Success(
            BuildOnlineDataHandoff(prepared));

    }

    public async Task<Result<InstallationResetOnlineDataHandoff?>> ReadAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result<InstallationResetActiveRecord?> activeRead = await activeStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            return Result<InstallationResetOnlineDataHandoff?>.Failure(activeRead.Error);

        }

        if (activeRead.Value is not { } active)
        {

            return Result<InstallationResetOnlineDataHandoff?>.Success(null);

        }

        Result validation = await ValidateResumeAsync(
            active,
            request,
            cancellationToken).ConfigureAwait(false);

        if (validation.IsFailure)
        {

            return Result<InstallationResetOnlineDataHandoff?>.Failure(validation.Error);

        }

        Result<InstallationResetOnlineDataHandoff> handoff = ReadPreparedHandoff(
            active,
            request);

        return handoff.IsSuccess
            ? Result<InstallationResetOnlineDataHandoff?>.Success(handoff.Value)
            : Result<InstallationResetOnlineDataHandoff?>.Failure(handoff.Error);

    }

    public async Task<Result> RecordCompletedAsync(
        InstallationResetOnlineDataHandoff handoff,
        DataRetentionApplyResult result,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(handoff);

        ArgumentNullException.ThrowIfNull(result);

        if (!IsTrustedOnlineDataCompletion(handoff, result))
        {

            return OnlineCompletionMismatch();

        }

        Result<InstallationResetActiveRecord?> activeRead = await activeStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            return Result.Failure(activeRead.Error);

        }

        if (activeRead.Value is not { } active
            || !MatchesPreparedHandoff(active, handoff))
        {

            return OnlineCompletionMismatch();

        }

        InstallationResetOnlineDataCompletion proof = new(
            result.OperationId,
            handoff.RequestedOperationId,
            handoff.DataPlanId,
            result.RowsDeleted,
            result.FilesDeleted,
            result.EstimatedBytesDeleted,
            result.DerivedRecordsDeleted);

        if (active.OnlineDataCompletion is { } existing)
        {

            return existing == proof
                ? Result.Success()
                : OnlineCompletionMismatch();

        }

        return await activeStore.WriteAsync(
            active with
            {
                OnlineDataCompletion = proof,
                LastErrorCode = null,
            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result> RetirePreEffectAsync(
        InstallationResetOnlineDataHandoff handoff,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(handoff);

        Result<InstallationResetActiveRecord?> activeRead = await activeStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            return Result.Failure(activeRead.Error);

        }

        if (activeRead.Value is not { } active)
        {

            return Result.Success();

        }

        if (!MatchesPreparedHandoff(active, handoff))
        {

            return ResumeMismatch();

        }

        if (handoff.DataResetCompleted
            || active.PointOfNoReturn
            || active.OnlineDataCompletion is not null)
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The online data reset outcome must be recovered before retirement."));

        }

        return await activeStore.RetirePreEffectAsync(
            handoff,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result<InstallationResetActiveRecord?> activeRead = await activeStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(activeRead.Error);

        }

        InstallationResetActiveRecord active;

        InstallationResetPlan plan;

        if (activeRead.Value is { } existing)
        {

            Result validation = await ValidateResumeAsync(
                existing,
                request,
                cancellationToken).ConfigureAwait(false);

            if (validation.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(validation.Error);

            }

            active = existing;

            plan = ReproduceAcceptedPlan(existing);

        }
        else
        {

            Result<InstallationResetPlan> replanned = await PlanAsync(
                request.Request,
                cancellationToken).ConfigureAwait(false);

            if (replanned.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(replanned.Error);

            }

            plan = replanned.Value;

            if (!string.Equals(request.ExpectedPlanId, plan.PlanId, StringComparison.Ordinal))
            {

                return Result<InstallationResetResult>.Failure(new Error(
                    ErrorCodes.Data.PlanChanged,
                    "The installation reset plan changed after confirmation."));

            }

            if (!plan.CredentialInventoryAvailable)
            {

                return Result<InstallationResetResult>.Failure(new Error(
                    ErrorCodes.Data.CredentialInventoryUnavailable,
                    "The accepted credential inventory is unavailable."));

            }

            if (plan.Blockers.Length > 0)
            {

                return Result<InstallationResetResult>.Failure(new Error(
                    ErrorCodes.Data.Blocked,
                    plan.Blockers[0].Message));

            }

            active = new InstallationResetActiveRecord(
                InstallationResetActiveStore.CurrentVersion,
                Guid.NewGuid(),
                plan.PlanId,
                plan.Scope,
                plan.Workspace,
                plan.AcceptedBinding,
                InstallationResetPhase.Prepared,
                PointOfNoReturn: false,
                RowsDeleted: 0,
                FilesDeleted: 0,
                EstimatedBytesDeleted: 0,
                CredentialResults: [],
                LastErrorCode: null);

            Result published = await activeStore.WriteAsync(active, cancellationToken)
                .ConfigureAwait(false);

            if (published.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(published.Error);

            }

        }

        InstallationResetApplyProgress progress = new(active);

        try
        {

            return await ContinueApplyAsync(progress, plan, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            InstallationResetActiveRecord current = progress.Active;

            InstallationResetActiveRecord cancelled = current with
            {
                PointOfNoReturn = current.PointOfNoReturn
                    || current.Phase is not InstallationResetPhase.Prepared,
                LastErrorCode = ErrorCodes.Data.RecoveryRequired,
            };

            Result checkpoint = await activeStore.WriteAsync(
                cancelled,
                CancellationToken.None).ConfigureAwait(false);

            return checkpoint.IsFailure
                ? Resumable(cancelled, checkpoint.Error)
                : Resumable(
                    cancelled,
                    new Error(
                        ErrorCodes.Data.RecoveryRequired,
                        "Installation reset was cancelled after its active record was published."));

        }

    }

    private async Task<Result<InstallationResetResult>> ContinueApplyAsync(
        InstallationResetApplyProgress progress,
        InstallationResetPlan plan,
        CancellationToken cancellationToken)
    {

        InstallationResetActiveRecord active = progress.Active;

        if (active.Phase is InstallationResetPhase.Completed)
        {

            return await ReturnCompletedAsync(
                active,
                plan,
                cancellationToken)
                .ConfigureAwait(false);

        }

        if (active.Phase is InstallationResetPhase.Prepared)
        {

            InstallationResetOnlineDataCompletion? onlineCompletion = null;

            if (active.DataHandoff is not null
                || active.OnlineDataCompletion is not null)
            {

                if (!TryGetOnlineDataCompletion(active, out onlineCompletion))
                {

                    return Resumable(
                        active,
                        new Error(
                            ErrorCodes.Data.RecoveryRequired,
                            "The authenticated host data reset requires recovery."));

                }

            }

            Result preData = active.Scope is InstallationResetScope.Workspace
                ? Result.Success()
                : await _preDataMutation
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);

            if (preData.IsFailure)
            {

                active = active with { LastErrorCode = preData.Error.Code };

                Result preDataCheckpoint = await activeStore.WriteAsync(
                    active,
                    cancellationToken).ConfigureAwait(false);

                return preDataCheckpoint.IsFailure
                    ? Resumable(active, preDataCheckpoint.Error)
                    : Resumable(active, preData.Error);

            }

            if (active.AcceptedBinding.DataPlanIds.Length > 0)
            {

                active = active with { PointOfNoReturn = true };

                progress.Active = active;

                Result pointOfNoReturnCheckpoint = await activeStore.WriteAsync(
                    active,
                    cancellationToken).ConfigureAwait(false);

                if (pointOfNoReturnCheckpoint.IsFailure)
                {

                    return Resumable(active, pointOfNoReturnCheckpoint.Error);

                }

                if (onlineCompletion is not null)
                {

                    active = active with
                    {
                        Phase = InstallationResetPhase.DataResetComplete,
                        PointOfNoReturn = true,
                        RowsDeleted = onlineCompletion.RowsDeleted,
                        FilesDeleted = onlineCompletion.FilesDeleted,
                        EstimatedBytesDeleted = onlineCompletion.EstimatedBytesDeleted,
                        LastErrorCode = null,
                    };

                }
                else
                {

                    string dataPlanId = active.AcceptedBinding.DataPlanIds[0];

                    DataRetentionRequest dataRequest = active.Scope is InstallationResetScope.Workspace
                        ? new DataRetentionRequest(
                            DataRetentionOperation.ResetWorkspace,
                            Workspace: active.Workspace)
                        : new DataRetentionRequest(DataRetentionOperation.FactoryReset);

                    Result<DataRetentionApplyResult> applied = await dataService.ApplyAsync(
                        new DataRetentionApplyRequest(dataRequest, dataPlanId),
                        cancellationToken).ConfigureAwait(false);

                    if (applied.IsFailure)
                    {

                        if (applied.Error.Code is ErrorCodes.Data.RecoveryRequired
                                or ErrorCodes.Data.ReconciliationFailed)
                        {

                            active = active with
                            {
                                PointOfNoReturn = true,
                                LastErrorCode = ErrorCodes.Data.RecoveryRequired,
                            };

                            Result recoveryCheckpoint = await activeStore.WriteAsync(
                                active,
                                cancellationToken).ConfigureAwait(false);

                            return recoveryCheckpoint.IsFailure
                                ? Resumable(active, recoveryCheckpoint.Error)
                                : Resumable(
                                active,
                                new Error(
                                    ErrorCodes.Data.RecoveryRequired,
                                    "The canonical data reset outcome requires recovery."));

                        }

                        Result retired = await activeStore.RetireAsync(
                            active.OperationId,
                            cancellationToken).ConfigureAwait(false);

                        return retired.IsSuccess
                            ? Result<InstallationResetResult>.Failure(applied.Error)
                            : Resumable(active, retired.Error);

                    }

                    DataRetentionApplyResult dataResult = applied.Value;

                    active = active with
                    {
                        Phase = InstallationResetPhase.DataResetComplete,
                        PointOfNoReturn = true,
                        RowsDeleted = dataResult.RowsDeleted,
                        FilesDeleted = dataResult.FilesDeleted,
                        EstimatedBytesDeleted = dataResult.EstimatedBytesDeleted,
                        LastErrorCode = null,
                    };

                }

                progress.Active = active;

            }
            else
            {

                active = active with
                {
                    Phase = InstallationResetPhase.DataResetComplete,
                    LastErrorCode = null,
                };

                progress.Active = active;

            }

            Result dataCheckpoint = await activeStore.WriteAsync(
                active,
                cancellationToken).ConfigureAwait(false);

            if (dataCheckpoint.IsFailure)
            {

                return Resumable(active, dataCheckpoint.Error);

            }

        }

        if (active.Phase is InstallationResetPhase.DataResetComplete)
        {

            Result<InstallationResetOfflineCleanupResult> cleaned = await offlineCleanup
                .ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);

            if (cleaned.IsFailure)
            {

                active = active with { LastErrorCode = cleaned.Error.Code };

                Result failureCheckpoint = await activeStore.WriteAsync(
                    active,
                    cancellationToken).ConfigureAwait(false);

                return failureCheckpoint.IsFailure
                    ? Resumable(active, failureCheckpoint.Error)
                    : Resumable(active, cleaned.Error);

            }

            InstallationResetOfflineCleanupResult cleanup = cleaned.Value;

            active = active with
            {
                PointOfNoReturn = active.PointOfNoReturn
                    || cleanup.FilesDeleted > 0,
                FilesDeleted = active.FilesDeleted + cleanup.FilesDeleted,
                EstimatedBytesDeleted = active.EstimatedBytesDeleted
                    + cleanup.EstimatedBytesDeleted,
                CredentialResults = MergeCredentialResults(
                    active.CredentialResults,
                    cleanup.CredentialResults,
                    acceptedAccounts: null),
            };

            progress.Active = active;

            if (!cleanup.Verification.Succeeded)
            {

                active = active with
                {
                    LastErrorCode = ErrorCodes.Data.ReconciliationFailed,
                };

                Result verificationCheckpoint = await activeStore.WriteAsync(
                    active,
                    cancellationToken).ConfigureAwait(false);

                if (verificationCheckpoint.IsFailure)
                {

                    return Resumable(active, verificationCheckpoint.Error);

                }

                return Result<InstallationResetResult>.Success(BuildResult(
                    active,
                    cleanup.PreservedBackups,
                    cleanup.Verification,
                    resumeRequired: true));

            }

            active = active with
            {
                Phase = InstallationResetPhase.OfflineCleanupComplete,
                LastErrorCode = null,
            };

            progress.Active = active;

            Result cleanupCheckpoint = await activeStore.WriteAsync(
                active,
                cancellationToken).ConfigureAwait(false);

            if (cleanupCheckpoint.IsFailure)
            {

                return Resumable(active, cleanupCheckpoint.Error);

            }

        }

        if (active.Phase is InstallationResetPhase.OfflineCleanupComplete)
        {

            InstallationResetCredentialResult[] deletedCredentials =
                credentialService.DeleteAndVerify(
                    active.AcceptedBinding.CredentialAccounts);

            InstallationResetCredentialResult[] mergedCredentials =
                MergeCredentialResults(
                    active.CredentialResults,
                    deletedCredentials,
                    active.AcceptedBinding.CredentialAccounts);

            InstallationResetVerification credentialVerification =
                VerifyCredentials(mergedCredentials);

            active = active with
            {
                PointOfNoReturn = active.PointOfNoReturn
                    || mergedCredentials.Any(static result =>
                        result.Status is InstallationResetItemStatus.Deleted),
                CredentialResults = mergedCredentials,
                LastErrorCode = credentialVerification.Succeeded
                    ? null
                    : ErrorCodes.Data.ReconciliationFailed,
            };

            progress.Active = active;

            if (!credentialVerification.Succeeded)
            {

                Result credentialCheckpoint = await activeStore.WriteAsync(
                    active,
                    cancellationToken).ConfigureAwait(false);

                return credentialCheckpoint.IsFailure
                    ? Resumable(active, credentialCheckpoint.Error)
                    : Result<InstallationResetResult>.Success(BuildResult(
                        active,
                        active.AcceptedBinding.PreservedBackups,
                        credentialVerification,
                        resumeRequired: true));

            }

            active = active with { Phase = InstallationResetPhase.Verified };

            progress.Active = active;

            Result verifiedCheckpoint = await activeStore.WriteAsync(
                active,
                cancellationToken).ConfigureAwait(false);

            if (verifiedCheckpoint.IsFailure)
            {

                return Resumable(active, verifiedCheckpoint.Error);

            }

        }

        if (active.Phase is InstallationResetPhase.Verified)
        {

            active = active with
            {
                Phase = InstallationResetPhase.Completed,
                LastErrorCode = null,
            };

            progress.Active = active;

            Result completedCheckpoint = await activeStore.WriteAsync(
                active,
                cancellationToken).ConfigureAwait(false);

            if (completedCheckpoint.IsFailure)
            {

                return Resumable(active, completedCheckpoint.Error);

            }

        }

        return await ReturnCompletedAsync(
            active,
            plan,
            cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<Result> ValidateResumeAsync(
        InstallationResetActiveRecord active,
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken)
    {

        if (active.Scope != request.Request.Scope
            || !string.Equals(
                active.PlanId,
                request.ExpectedPlanId,
                StringComparison.Ordinal))
        {

            return ResumeMismatch();

        }

        if (active.Scope is InstallationResetScope.Global)
        {

            return active.Workspace is null
                ? Result.Success()
                : ResumeMismatch();

        }

        if (active.Scope is InstallationResetScope.All)
        {

            if (active.Workspace is null
                || !InvocationBelongsToAcceptedWorkspace(
                    request.Request.InvocationDirectory,
                    active.Workspace,
                    active.AcceptedBinding.ExcludedRoots))
            {

                return ResumeMismatch();

            }

            if (active.PointOfNoReturn
                || IsPreparedOnlineDataHandoff(active))
            {

                return Result.Success();

            }

        }

        if (active.Workspace is null || workspaceResolver is null)
        {

            return ResumeMismatch();

        }

        Result<InstallationResetWorkspaceResolution> resolved = await workspaceResolver
            .ResolveAsync(request.Request.InvocationDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (resolved.IsFailure || !SameWorkspace(active.Workspace, resolved.Value.Workspace))
        {

            return ResumeMismatch();

        }

        return Result.Success();

    }

    private async Task<Result<InstallationResetResult>> ReturnCompletedAsync(
        InstallationResetActiveRecord active,
        InstallationResetPlan plan,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetOfflineCleanupResult> finalCleanup = await offlineCleanup
            .ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);

        if (finalCleanup.IsFailure)
        {

            return Resumable(active, finalCleanup.Error);

        }

        InstallationResetOfflineCleanupResult cleanup = finalCleanup.Value;

        active = active with
        {
            PointOfNoReturn = active.PointOfNoReturn || cleanup.FilesDeleted > 0,
            FilesDeleted = active.FilesDeleted + cleanup.FilesDeleted,
            EstimatedBytesDeleted = active.EstimatedBytesDeleted
                + cleanup.EstimatedBytesDeleted,
        };

        if (!cleanup.Verification.Succeeded)
        {

            active = active with
            {
                LastErrorCode = ErrorCodes.Data.ReconciliationFailed,
            };

            Result verificationCheckpoint = await activeStore.WriteAsync(
                active,
                cancellationToken).ConfigureAwait(false);

            return verificationCheckpoint.IsFailure
                ? Resumable(active, verificationCheckpoint.Error)
                : Result<InstallationResetResult>.Success(BuildResult(
                    active,
                    cleanup.PreservedBackups,
                    cleanup.Verification,
                    resumeRequired: true));

        }

        InstallationResetCredentialResult[] credentialResults =
            credentialService.DeleteAndVerify(
                active.AcceptedBinding.CredentialAccounts);

        active = active with
        {
            PointOfNoReturn = active.PointOfNoReturn
                || credentialResults.Any(static result =>
                    result.Status is InstallationResetItemStatus.Deleted),
            CredentialResults = MergeCredentialResults(
                active.CredentialResults,
                credentialResults,
                active.AcceptedBinding.CredentialAccounts),
        };

        InstallationResetVerification verification = VerifyCompleted(active);

        if (!verification.Succeeded)
        {

            return Result<InstallationResetResult>.Success(BuildResult(
                active,
                active.AcceptedBinding.PreservedBackups,
                verification,
                resumeRequired: true));

        }

        InstallationResetResult final = BuildResult(
            active,
            active.AcceptedBinding.PreservedBackups,
            verification,
            resumeRequired: false);

        Result retired = await activeStore.RetireAsync(
            active.OperationId,
            cancellationToken).ConfigureAwait(false);

        return retired.IsSuccess
            ? Result<InstallationResetResult>.Success(final)
            : Resumable(active, retired.Error);

    }

    private static Result<InstallationResetResult> Resumable(
        InstallationResetActiveRecord active,
        Error error) =>
        Result<InstallationResetResult>.Success(BuildResult(
            active with { LastErrorCode = error.Code },
            active.AcceptedBinding.PreservedBackups,
            new InstallationResetVerification(
                false,
                [new InstallationResetIssueSummary(error.Code, error.Message)]),
            resumeRequired: true));

    private InstallationResetPlan ReproduceAcceptedPlan(
        InstallationResetActiveRecord active) =>
        new(
            active.PlanId,
            active.Scope,
            active.Workspace,
            _timeProvider.GetUtcNow(),
            DataInventoryAvailable: active.AcceptedBinding.DataPlanIds.Length > 0,
            CredentialInventoryAvailable: true,
            Targets: [],
            Credentials:
            [
                .. active.AcceptedBinding.CredentialAccounts.Select(
                    static account => new InstallationResetCredentialSummary(
                        account,
                        InstallationResetItemStatus.Pending)),
            ],
            active.AcceptedBinding.PreservedBackups,
            Exclusions: [],
            Blockers: [],
            Rows: null,
            Files: active.FilesDeleted,
            EstimatedBytes: active.EstimatedBytesDeleted,
            active.AcceptedBinding);

    private static InstallationResetCredentialResult[] MergeCredentialResults(
        InstallationResetCredentialResult[] existing,
        InstallationResetCredentialResult[] updates,
        string[]? acceptedAccounts)
    {

        Dictionary<string, InstallationResetCredentialResult> merged =
            new(StringComparer.Ordinal);

        foreach (InstallationResetCredentialResult result in existing)
        {

            merged[result.Account] = result;

        }

        if (acceptedAccounts is null)
        {

            foreach (InstallationResetCredentialResult result in updates)
            {

                merged[result.Account] = result;

            }

            return [.. merged.Values.OrderBy(static item => item.Account, StringComparer.Ordinal)];

        }

        Dictionary<string, InstallationResetCredentialResult> admittedUpdates = updates
            .Where(item => acceptedAccounts.Contains(item.Account, StringComparer.Ordinal))
            .GroupBy(static item => item.Account, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last(),
                StringComparer.Ordinal);

        foreach (string account in acceptedAccounts
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {

            merged[account] = admittedUpdates.TryGetValue(account, out var result)
                ? result
                : new InstallationResetCredentialResult(
                    account,
                    InstallationResetItemStatus.Failed,
                    ErrorCodes.Data.ReconciliationFailed);

        }

        return [.. merged.Values.OrderBy(static item => item.Account, StringComparer.Ordinal)];

    }

    private static InstallationResetVerification VerifyCredentials(
        InstallationResetCredentialResult[] credentials)
    {

        InstallationResetIssueSummary[] issues =
        [
            .. credentials
                .Where(static item => !CredentialIsRemoved(item))
                .Select(static item => new InstallationResetIssueSummary(
                    item.ErrorCode ?? ErrorCodes.Data.ReconciliationFailed,
                    "An accepted credential could not be verified as absent.",
                    item.Account)),
        ];

        return new InstallationResetVerification(issues.Length == 0, issues);

    }

    private static InstallationResetVerification VerifyCompleted(
        InstallationResetActiveRecord active)
    {

        InstallationResetVerification credentialVerification = VerifyCredentials(
            active.CredentialResults);

        if (active.LastErrorCode is null)
        {

            return credentialVerification;

        }

        InstallationResetIssueSummary lastError = new(
            active.LastErrorCode,
            "The installation reset requires recovery before completion.");

        return new InstallationResetVerification(
            false,
            [.. credentialVerification.RemainingIssues, lastError]);

    }

    private static bool CredentialIsRemoved(
        InstallationResetCredentialResult result) =>
        result.Status is InstallationResetItemStatus.Deleted
            or InstallationResetItemStatus.Absent;

    private static bool SameWorkspace(
        DataRetentionWorkspaceBinding expected,
        DataRetentionWorkspaceBinding current)
    {

        if (expected.CampaignId != current.CampaignId)
        {

            return false;

        }

        try
        {

            string expectedRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(expected.WorkspaceRoot));

            string currentRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(current.WorkspaceRoot));

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(expectedRoot, currentRoot, comparison);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;

        }

    }

    private static bool InvocationBelongsToAcceptedWorkspace(
        string invocationDirectory,
        DataRetentionWorkspaceBinding workspace,
        string[] excludedRoots)
    {

        try
        {

            string root = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(workspace.WorkspaceRoot));

            string invocation = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(invocationDirectory));

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            bool belongsToWorkspace = string.Equals(root, invocation, comparison)
                || invocation.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    comparison);

            if (!belongsToWorkspace)
            {

                return false;

            }

            return !excludedRoots.Any(excludedRoot =>
            {

                string excluded = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(excludedRoot));

                return string.Equals(excluded, invocation, comparison)
                    || invocation.StartsWith(
                        excluded + Path.DirectorySeparatorChar,
                        comparison);

            });

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return false;

        }

    }

    private static Result ResumeMismatch() =>
        Result.Failure(new Error(
            ErrorCodes.Data.ResetInProgress,
            "A different installation reset owns the active operation."));

    private static Result<T> PlanChanged<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.PlanChanged,
            "The canonical data plan changed after the installation plan was created."));

    private static Result OnlineCompletionMismatch() =>
        Result.Failure(new Error(
            ErrorCodes.Data.ReconciliationFailed,
            "The authenticated host data reset completion proof did not reconcile."));

    private static bool IsTrustedOnlineDataCompletion(
        InstallationResetOnlineDataHandoff handoff,
        DataRetentionApplyResult result) =>
        handoff.RequestedOperationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(handoff.InstallationPlanId)
        && !string.IsNullOrWhiteSpace(handoff.DataPlanId)
        && result.OperationId != Guid.Empty
        && result.OperationId != handoff.RequestedOperationId
        && result.RequestedOperationId == handoff.RequestedOperationId
        && string.Equals(result.PlanId, handoff.DataPlanId, StringComparison.Ordinal)
        && result.Reconciled
        && result.Blockers.Length == 0
        && result.Conflicts.Length == 0
        && result.RowsDeleted >= 0
        && result.FilesDeleted >= 0
        && result.EstimatedBytesDeleted >= 0
        && result.DerivedRecordsDeleted >= 0;

    private static bool MatchesPreparedHandoff(
        InstallationResetActiveRecord active,
        InstallationResetOnlineDataHandoff handoff) =>
        active.Scope is InstallationResetScope.Global or InstallationResetScope.All
        && active.Phase is InstallationResetPhase.Prepared
        && active.DataHandoff is InstallationResetDataHandoff.HostFactoryErasure
        && active.AcceptedBinding.DataPlanIds.Length == 1
        && active.OperationId == handoff.RequestedOperationId
        && string.Equals(
            active.PlanId,
            handoff.InstallationPlanId,
            StringComparison.Ordinal)
        && string.Equals(
            active.AcceptedBinding.DataPlanIds[0],
            handoff.DataPlanId,
            StringComparison.Ordinal);

    private static bool TryGetOnlineDataCompletion(
        InstallationResetActiveRecord active,
        out InstallationResetOnlineDataCompletion? completion)
    {

        completion = active.OnlineDataCompletion;

        return active.Scope is InstallationResetScope.Global or InstallationResetScope.All
            && active.Phase is InstallationResetPhase.Prepared
            && active.DataHandoff is InstallationResetDataHandoff.HostFactoryErasure
            && active.AcceptedBinding.DataPlanIds.Length == 1
            && completion is not null
            && completion.ServerOperationId != Guid.Empty
            && completion.ServerOperationId != active.OperationId
            && completion.RequestedOperationId == active.OperationId
            && string.Equals(
                completion.DataPlanId,
                active.AcceptedBinding.DataPlanIds[0],
                StringComparison.Ordinal)
            && completion.RowsDeleted >= 0
            && completion.FilesDeleted >= 0
            && completion.EstimatedBytesDeleted >= 0
            && completion.DerivedRecordsDeleted >= 0;

    }

    private static bool IsPreparedOnlineDataHandoff(
        InstallationResetActiveRecord active) =>
        active.Scope is InstallationResetScope.Global or InstallationResetScope.All
        && active.Phase is InstallationResetPhase.Prepared
        && active.DataHandoff is InstallationResetDataHandoff.HostFactoryErasure
        && active.AcceptedBinding.DataPlanIds.Length == 1
        && !string.IsNullOrWhiteSpace(active.AcceptedBinding.DataPlanIds[0])
        && (active.OnlineDataCompletion is null
            || TryGetOnlineDataCompletion(active, out _));

    private static Result<InstallationResetOnlineDataHandoff> ReadPreparedHandoff(
        InstallationResetActiveRecord active,
        InstallationResetApplyRequest request)
    {

        if (active.Scope is not (InstallationResetScope.Global or InstallationResetScope.All)
            || active.Scope != request.Request.Scope
            || active.Phase is not InstallationResetPhase.Prepared
            || active.DataHandoff is not InstallationResetDataHandoff.HostFactoryErasure
            || active.AcceptedBinding.DataPlanIds.Length != 1
            || !string.Equals(
                active.PlanId,
                request.ExpectedPlanId,
                StringComparison.Ordinal))
        {

            return Result<InstallationResetOnlineDataHandoff>.Failure(new Error(
                ErrorCodes.Data.ResetInProgress,
                "A different installation reset owns the active operation."));

        }

        return Result<InstallationResetOnlineDataHandoff>.Success(
            BuildOnlineDataHandoff(active));

    }

    private static InstallationResetOnlineDataHandoff BuildOnlineDataHandoff(
        InstallationResetActiveRecord active) =>
        new(
            active.OperationId,
            active.PlanId,
            active.AcceptedBinding.DataPlanIds[0],
            active.OnlineDataCompletion is not null);

    private static bool SameAcceptedInstallationPlan(
        InstallationResetPlan current,
        InstallationResetPlan confirmed) =>
        string.Equals(current.PlanId, confirmed.PlanId, StringComparison.Ordinal)
        && current.Scope == confirmed.Scope
        && current.Workspace == confirmed.Workspace
        && current.DataInventoryAvailable == confirmed.DataInventoryAvailable
        && current.CredentialInventoryAvailable == confirmed.CredentialInventoryAvailable
        && current.Targets.SequenceEqual(confirmed.Targets)
        && current.Credentials.SequenceEqual(confirmed.Credentials)
        && current.PreservedBackups.SequenceEqual(confirmed.PreservedBackups)
        && current.Exclusions.SequenceEqual(confirmed.Exclusions)
        && current.Blockers.SequenceEqual(confirmed.Blockers)
        && current.Rows == confirmed.Rows
        && current.Files == confirmed.Files
        && current.EstimatedBytes == confirmed.EstimatedBytes
        && SameAcceptedBinding(current.AcceptedBinding, confirmed.AcceptedBinding);

    private static bool SameAcceptedBinding(
        InstallationResetAcceptedBinding current,
        InstallationResetAcceptedBinding confirmed) =>
        string.Equals(current.BindingId, confirmed.BindingId, StringComparison.Ordinal)
        && current.SelectedRoots.SequenceEqual(confirmed.SelectedRoots, PathComparer)
        && current.ExcludedRoots.SequenceEqual(confirmed.ExcludedRoots, PathComparer)
        && current.PreservedBackups.SequenceEqual(confirmed.PreservedBackups)
        && current.CredentialAccounts.SequenceEqual(
            confirmed.CredentialAccounts,
            StringComparer.Ordinal)
        && current.DataPlanIds.SequenceEqual(
            confirmed.DataPlanIds,
            StringComparer.Ordinal);

    private static bool SameOrdinaryDataPlan(
        DataRetentionPlan local,
        DataRetentionPlan online) =>
        local.Request == online.Request
        && local.Items.SequenceEqual(online.Items)
        && local.Blockers.SequenceEqual(online.Blockers)
        && local.Conflicts.SequenceEqual(online.Conflicts)
        && local.Rows == online.Rows
        && local.Files == online.Files
        && local.EstimatedBytes == online.EstimatedBytes
        && local.DerivedRecords == online.DerivedRecords
        && local.CandidateIds.SequenceEqual(online.CandidateIds, StringComparer.Ordinal)
        && local.RequiresConfirmation == online.RequiresConfirmation;

    private static InstallationResetResult BuildResult(
        InstallationResetActiveRecord active,
        InstallationResetPreservedBackup[] backups,
        InstallationResetVerification verification,
        bool resumeRequired) =>
        new(
            active.OperationId,
            active.PlanId,
            active.Scope,
            active.Phase,
            active.PointOfNoReturn,
            active.RowsDeleted,
            active.FilesDeleted,
            active.EstimatedBytesDeleted,
            active.CredentialResults,
            backups,
            verification,
            resumeRequired,
            active.LastErrorCode);

    private static string ComputeBindingId(
        InstallationResetPlanRequest request,
        InstallationResetAcceptedBinding binding) =>
        Hash(string.Join(
            '|',
            request.Scope,
            string.Join(',', binding.SelectedRoots),
            string.Join(',', binding.ExcludedRoots),
            string.Join(',', binding.PreservedBackups.Select(
                static backup => $"{backup.CanonicalPath}:{backup.Identity.Value}:{backup.Identity.Length}:{backup.Identity.HardLinkCount}")),
            string.Join(',', binding.CredentialAccounts),
            string.Join(',', binding.DataPlanIds)));

    private static string ComputePlanId(
        InstallationResetPlanRequest request,
        InstallationResetAcceptedBinding binding,
        DataRetentionPlan? data,
        InstallationResetCredentialSummary[] credentials,
        InstallationResetTargetDescriptor[] fileTargets) =>
        Hash(string.Join(
            '|',
            request.Scope,
            binding.BindingId,
            data?.PlanId ?? "unavailable",
            data?.Rows.ToString(CultureInfo.InvariantCulture) ?? "unavailable",
            data?.Files.ToString(CultureInfo.InvariantCulture) ?? "unavailable",
            string.Join(',', credentials.Select(static item => $"{item.Account}:{item.Status}")),
            string.Join(',', fileTargets
                .OrderBy(static target => target.ResourceId, StringComparer.Ordinal)
                .ThenBy(static target => target.CanonicalPath, StringComparer.Ordinal)
                .Select(static target => string.Join(
                    ':',
                    target.ResourceId,
                    target.CanonicalPath,
                    target.Identity?.Value,
                    target.Identity?.Length.ToString(CultureInfo.InvariantCulture),
                    target.Identity?.HardLinkCount.ToString(CultureInfo.InvariantCulture),
                    target.Files.ToString(CultureInfo.InvariantCulture),
                    target.EstimatedBytes.ToString(CultureInfo.InvariantCulture))))));

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class InstallationResetApplyProgress(
        InstallationResetActiveRecord active)
    {

        public InstallationResetActiveRecord Active { get; set; } = active;

    }

    private static bool PathsOverlap(string left, string right)
    {

        try
        {

            string normalizedLeft = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(left));

            string normalizedRight = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(right));

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return IsWithinOrEqual(normalizedLeft, normalizedRight, comparison)
                || IsWithinOrEqual(normalizedRight, normalizedLeft, comparison);

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {

            return true;

        }

    }

    private static bool IsWithinOrEqual(
        string root,
        string candidate,
        StringComparison comparison) =>
        string.Equals(root, candidate, comparison)
        || candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            comparison);

}
