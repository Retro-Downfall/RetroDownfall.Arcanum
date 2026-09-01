using System.Buffers.Binary;

using System.Collections.Concurrent;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IInstallationResetLockedService
{

    Task<Result<InstallationResetResult>> ApplyFullUnderMaintenanceLockAsync(
        FullInstallationResetRequest request,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

    Task<Result<InstallationResetResult>> ApplyUnderMaintenanceLockAsync(
        InstallationResetApplyRequest request,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

    Task<Result<InstallationResetResult>> ApplyFreshUnderMaintenanceLockAsync(
        InstallationResetPlanRequest request,
        StoppedHostInstallationResetPlan confirmedPlan,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default);

}

internal interface IInstallationResetActiveWriter
{

    Task<Result> WriteAsync(
        InstallationResetActiveRecord record,
        CancellationToken cancellationToken);

    Task<Result> RetireAsync(
        Guid operationId,
        CancellationToken cancellationToken);

}

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
    InstallationResetControlPaths? controlPaths = null,
    IInstallationResetDatabaseIdentityReader? identityReader = null,
    IInstallationResetHostProcessToolsPairReader? pairReader = null,
    IFullInstallationResetRemediationAttestationVerifier? remediationVerifier = null,
    Func<IHostToolsMarkerPairResetCoordinator>? markerPairReset = null,
    Func<IFullInstallationResetTerminalContinuation>? terminalContinuation = null,
    IInstallationResetStoppedHostDataService? stoppedHostDataService = null,
    IInstallationResetStoppedHostProcessToolsPairReader? stoppedHostPairReader = null,
    string? canonicalDatabasePath = null)
    : IInstallationResetService,
      IInstallationResetOnlineDataHandoff,
      IInstallationResetLockedService,
      IInstallationResetStoppedHostPlanner
{

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private readonly IInstallationResetStateRoots _stateRoots =
        stateRoots ?? InstallationResetStateRoots.Default;

    private readonly IInstallationResetPreDataMutation _preDataMutation =
        preDataMutation ?? NoopInstallationResetPreDataMutation.Instance;

    private readonly IInstallationResetHostProcessToolsPairReader _pairReader =
        pairReader ?? throw new ArgumentNullException(nameof(pairReader));

    private readonly IInstallationResetStoppedHostDataService? _stoppedHostDataService =
        stoppedHostDataService ?? dataService as IInstallationResetStoppedHostDataService;

    private readonly IInstallationResetStoppedHostProcessToolsPairReader?
        _stoppedHostPairReader = stoppedHostPairReader
            ?? pairReader as IInstallationResetStoppedHostProcessToolsPairReader;

    private readonly string _canonicalDatabasePath = Path.GetFullPath(
        canonicalDatabasePath ?? ArcanumPaths.GrimoireDatabaseFile);

    private readonly ConcurrentDictionary<string, DataRetentionPlan> _localDataPlans =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, DataRetentionPlan> _onlineDataPlans =
        new(StringComparer.Ordinal);

    internal string GuardedRoot => activeStore.GuardedRoot;

    public Task<Result<InstallationResetPlan>> PlanAsync(
        InstallationResetPlanRequest request,
        CancellationToken cancellationToken = default) =>
        PlanCoreAsync(
            request,
            issuer: null,
            cancellationToken);

    public async Task<Result<StoppedHostInstallationResetPlan>>
        PlanUnderStoppedHostLockAsync(
            InstallationResetPlanRequest request,
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(issuer);

        if (_stoppedHostDataService is null
            || _stoppedHostPairReader is null)
        {

            return Result<StoppedHostInstallationResetPlan>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The stopped-host installation-reset planning path is unavailable."));

        }

        Result<Guid> installation = await _stoppedHostDataService
            .ReadIdentityUnderStoppedHostAuthorityAsync(
                issuer,
                cancellationToken).ConfigureAwait(false);

        if (installation.IsFailure || installation.Value == Guid.Empty)
        {

            return Result<StoppedHostInstallationResetPlan>.Failure(
                installation.IsFailure
                    ? installation.Error
                    : new Error(
                        ErrorCodes.Data.ControlPathUnavailable,
                        "The installation identity is unavailable."));

        }

        Result<InstallationResetPlan> planned = await PlanCoreAsync(
            request,
            issuer,
            cancellationToken).ConfigureAwait(false);

        if (planned.IsFailure)
        {

            return Result<StoppedHostInstallationResetPlan>.Failure(planned.Error);

        }

        DataRetentionPlan? dataPlan = _localDataPlans.GetValueOrDefault(
            planned.Value.PlanId);

        DataRetentionCovenantInventory? disclosure = dataPlan?.Covenant;

        if ((request.Scope is InstallationResetScope.Global or InstallationResetScope.All)
            && disclosure is null)
        {

            return Result<StoppedHostInstallationResetPlan>.Failure(new Error(
                ErrorCodes.Data.InventoryUnavailable,
                "The local Covenant inventory is unavailable."));

        }

        return Result<StoppedHostInstallationResetPlan>.Success(
            new StoppedHostInstallationResetPlan(
                planned.Value,
                request.Scope is InstallationResetScope.Workspace
                    ? null
                    : disclosure));

    }

    private async Task<Result<InstallationResetPlan>> PlanCoreAsync(
        InstallationResetPlanRequest request,
        IStoppedHostGrimoireAuthorityIssuer? issuer,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        DataRetentionWorkspaceBinding? workspace = null;

        string[] workspaceExclusions = [];

        if (request.Scope is InstallationResetScope.Workspace or InstallationResetScope.All)
        {

            if (issuer is null && workspaceResolver is null
                || issuer is not null && _stoppedHostDataService is null)
            {

                return Result<InstallationResetPlan>.Failure(new Error(
                    ErrorCodes.Data.InventoryUnavailable,
                    "The registered Campaign inventory is unavailable."));

            }

            Result<InstallationResetWorkspaceResolution> resolved = issuer is null
                ? await workspaceResolver!
                    .ResolveAsync(request.InvocationDirectory, cancellationToken)
                    .ConfigureAwait(false)
                : await _stoppedHostDataService!
                    .ResolveWorkspaceUnderStoppedHostAuthorityAsync(
                        request.InvocationDirectory,
                        issuer,
                        cancellationToken).ConfigureAwait(false);

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

        Result<DataRetentionPlan> dataPlan = issuer is null
            ? await dataService
                .PlanAsync(dataRequest, cancellationToken)
                .ConfigureAwait(false)
            : await _stoppedHostDataService!
                .PlanUnderStoppedHostAuthorityAsync(
                    dataRequest,
                    issuer,
                    cancellationToken).ConfigureAwait(false);

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

        bool externalRemediationRequired = false;

        if (request.Scope is InstallationResetScope.Global or InstallationResetScope.All)
        {

            Result<HostProcessToolsMarkerPairJoinResult> pair = issuer is null
                ? await _pairReader
                    .ReadAsync(cancellationToken).ConfigureAwait(false)
                : await _stoppedHostPairReader!
                    .ReadUnderStoppedHostAuthorityAsync(
                        issuer,
                        cancellationToken).ConfigureAwait(false);

            externalRemediationRequired = pair.IsFailure
                || pair.Value.Disposition is not HostProcessToolsMarkerPairDisposition.Clean;

        }

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
            .. externalRemediationRequired
                ? new InstallationResetIssueSummary[]
                {
                    new(
                        ErrorCodes.Data.ExternalRemediationRequired,
                        "The host-process-tools marker pair requires external remediation."),
                }
                : [],
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

    public Result<InstallationResetHostHandoff> CreateHostHandoff(
        InstallationResetApplyRequest request,
        InstallationResetPlan confirmedPlan)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(confirmedPlan);

        bool valid = request.Request.Scope is InstallationResetScope.Global
                or InstallationResetScope.All
            && confirmedPlan.Scope == request.Request.Scope
            && string.Equals(
                request.ExpectedPlanId,
                confirmedPlan.PlanId,
                StringComparison.Ordinal)
            && confirmedPlan.CredentialInventoryAvailable
            && confirmedPlan.Blockers.Length == 0
            && confirmedPlan.AcceptedBinding.DataPlanIds is { Length: 1 }
            && !string.IsNullOrWhiteSpace(
                confirmedPlan.AcceptedBinding.DataPlanIds[0])
            && _onlineDataPlans.ContainsKey(confirmedPlan.PlanId);

        return valid
            ? new InstallationResetHostHandoff(
                Guid.NewGuid(),
                confirmedPlan.PlanId,
                confirmedPlan.Scope,
                confirmedPlan.Workspace,
                confirmedPlan.AcceptedBinding)
            : PlanChanged<InstallationResetHostHandoff>();

    }

    public async Task<Result<InstallationResetHostHandoff?>> ReadAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result<InstallationResetActiveRecoveryState> recovered = await activeStore
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result<InstallationResetHostHandoff?>.Failure(recovered.Error);

        }

        InstallationResetActiveRecord? active = recovered.Value.Outcome switch
        {
            InstallationResetActiveRecoveryOutcome.AuthenticatedV2
                when recovered.Value.Publication is { } publication =>
                publication.Payload.ToRecord(),
            InstallationResetActiveRecoveryOutcome.LegacyV1 =>
                recovered.Value.LegacyRecord,
            _ => null,
        };

        if (active is null)
        {

            return Result<InstallationResetHostHandoff?>.Success(null);

        }

        Result validation = await ValidateResumeAsync(
            active,
            request,
            cancellationToken).ConfigureAwait(false);

        if (validation.IsFailure || !IsPreparedOnlineDataHandoff(active))
        {

            return validation.IsFailure
                ? Result<InstallationResetHostHandoff?>.Failure(validation.Error)
                : Result<InstallationResetHostHandoff?>.Failure(
                    ResumeMismatch().Error);

        }

        return Result<InstallationResetHostHandoff?>.Success(
            new InstallationResetHostHandoff(
                active.OperationId,
                active.PlanId,
                active.Scope,
                active.Workspace,
                active.AcceptedBinding));

    }

    public Task<Result<InstallationResetResult>> ApplyFullAsync(
        FullInstallationResetRequest request,
        CancellationToken cancellationToken = default)
    {

        Result validation = ValidateFullRequest(request);

        if (validation.IsFailure)
        {

            return Task.FromResult(Result<InstallationResetResult>.Failure(
                validation.Error));

        }

        return Task.FromResult(Result<InstallationResetResult>.Failure(new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            "Full installation reset requires the exact held maintenance lock.")));

    }

    public Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<InstallationResetResult>.Failure(new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            "Installation reset apply requires the exact held maintenance lock.")));

    public async Task<Result<InstallationResetResult>> ApplyFullUnderMaintenanceLockAsync(
        FullInstallationResetRequest request,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        Result requestValidation = ValidateFullRequest(request);

        if (requestValidation.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(requestValidation.Error);

        }

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(activeStore.GuardedRoot);

        StoppedHostGrimoireAuthorityIssuer issuer =
            CreateInstallationResetStoppedHostIssuer(heldInstallationLock);

        IInstallationResetDatabaseIdentityReader? effectiveIdentityReader =
            identityReader
            ?? activeStore as IInstallationResetDatabaseIdentityReader;

        if (effectiveIdentityReader is null
            || remediationVerifier is null
            || _stoppedHostDataService is null
            || _stoppedHostPairReader is null)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The authenticated full-reset control path is unavailable."));

        }

        Result<HostProcessToolsMarkerPairJoinResult> pair =
            await _stoppedHostPairReader.ReadUnderStoppedHostAuthorityAsync(
                issuer,
                cancellationToken).ConfigureAwait(false);

        if (pair.IsFailure
            || pair.Value.Disposition is not HostProcessToolsMarkerPairDisposition.TaintedMatched
            || pair.Value.MatchedPair is null)
        {

            return ExternalRemediationRequired<InstallationResetResult>();

        }

        Result<Guid> installation = await _stoppedHostDataService
            .ReadIdentityUnderStoppedHostAuthorityAsync(
                issuer,
                cancellationToken).ConfigureAwait(false);

        if (installation.IsFailure || installation.Value == Guid.Empty)
        {

            return ExternalRemediationInvalid<InstallationResetResult>();

        }

        Result<InstallationResetActiveRecoveryState> recovered = await activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(recovered.Error);

        }

        InstallationResetActivePublication? publication = recovered.Value.Publication;

        InstallationResetActiveRecord? existing = publication?.Payload.ToRecord();

        if (recovered.Value.Outcome is not (
                InstallationResetActiveRecoveryOutcome.NoActiveRecord
                or InstallationResetActiveRecoveryOutcome.AuthenticatedV2)
            || recovered.Value.Outcome is InstallationResetActiveRecoveryOutcome.NoActiveRecord
                && existing is not null
            || recovered.Value.Outcome is InstallationResetActiveRecoveryOutcome.AuthenticatedV2
                && existing is null)
        {

            return ExternalRemediationInvalid<InstallationResetResult>();

        }

        if (existing is not null)
        {

            Result<HostProcessToolsMatchedPair> retryPair =
                await ReadCurrentTaintedMatchedPairAsync(
                    issuer,
                    cancellationToken)
                    .ConfigureAwait(false);

            if (retryPair.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(retryPair.Error);

            }

            if (!SameFullRequest(existing, request)
                || existing.FullInstallationResetRemediationClaim is not
                    { Version: 1 } acceptedClaim
                || !remediationVerifier.MatchesAuthenticatedClaim(
                    request.ExternalRemediation,
                    installation.Value,
                    retryPair.Value,
                    acceptedClaim.OperationId,
                    acceptedClaim.InstallationId,
                    acceptedClaim.AttestationDigest,
                    acceptedClaim.NonceDigest,
                    acceptedClaim.IssuerDigest))
            {

                return ExternalRemediationInvalid<InstallationResetResult>();

            }

            await RunMarkerPairResetAsync(
                heldInstallationLock,
                publication,
                existing,
                request.ExternalRemediation,
                cancellationToken).ConfigureAwait(false);

            return await ContinueFullAsync(
                heldInstallationLock,
                existing,
                cancellationToken).ConfigureAwait(false);

        }

        Result<InstallationResetPlan> planned = await ReplanFullAsync(
            request.Apply,
            cancellationToken).ConfigureAwait(false);

        if (planned.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(planned.Error);

        }

        InstallationResetPlan plan = planned.Value;

        if (!plan.CredentialInventoryAvailable)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.CredentialInventoryUnavailable,
                "The accepted credential inventory is unavailable."));

        }

        if (plan.Blockers.Any(static blocker =>
                blocker.Code != ErrorCodes.Data.ExternalRemediationRequired))
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.Blocked,
                "The accepted full installation reset has an unresolved blocker."));

        }

        Result<HostProcessToolsMatchedPair> admissionPair =
            await ReadCurrentTaintedMatchedPairAsync(
                issuer,
                cancellationToken)
                .ConfigureAwait(false);

        if (admissionPair.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(admissionPair.Error);

        }

        Result<FullInstallationResetRemediationAuthorization> verified =
            remediationVerifier.Verify(
                request.ExternalRemediation,
                installation.Value,
                admissionPair.Value);

        if (verified.IsFailure
            || verified.Value.OperationId != request.OperationId
            || verified.Value.InstallationId != installation.Value)
        {

            return ExternalRemediationInvalid<InstallationResetResult>();

        }

        FullInstallationResetRemediationClaimV1 claim = Claim(verified.Value);

        InstallationResetActiveRecord active = new(
            InstallationResetActiveStore.CurrentVersion,
            request.OperationId,
            plan.PlanId,
            InstallationResetScope.All,
            plan.Workspace,
            plan.AcceptedBinding,
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: ErrorCodes.Data.RecoveryRequired,
            FullInstallationResetRemediationClaim: claim);

        AuthenticatedActiveWriter writer = new(
            activeStore,
            heldInstallationLock,
            activeStore.GuardedRoot,
            installation.Value,
            publication);

        Result published = await writer.WriteAsync(active, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(published.Error);

        }

        await RunMarkerPairResetAsync(
            heldInstallationLock,
            writer.Publication,
            active,
            request.ExternalRemediation,
            cancellationToken).ConfigureAwait(false);

        return await ContinueFullAsync(
            heldInstallationLock,
            active,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Continues an attested full reset past its marker-pair boundary, or reports it admitted.
    /// </summary>
    /// <remarks>
    /// The decision is made from the durable record rather than from what the coordinator returned,
    /// because the coordinator deliberately returns recovery-required on every path — the progress it
    /// makes is the checkpoint it publishes, and this is the reader of that checkpoint.
    ///
    /// <para>Only a managed-file reconciliation at <c>TerminalInventoryVerified</c> unlocks the rest.
    /// Short of that the installation still records files nothing has accounted for, and deleting the
    /// database that describes them would strand them with no record they ever existed. Anything less
    /// reports the operation admitted and recovery required, exactly as it did before this
    /// continuation existed.</para>
    /// </remarks>
    private async Task<Result<InstallationResetResult>> ContinueFullAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActiveRecord admitted,
        CancellationToken cancellationToken)
    {

        if (terminalContinuation is null)
        {

            return FullAdmissionAccepted(admitted);

        }

        Result<InstallationResetActiveRecoveryState> recovered = await activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken).ConfigureAwait(false);

        if (recovered.IsFailure
            || recovered.Value.Outcome
                is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } publication
            || publication.Payload.FullInstallationResetRemediationClaim is not { } claim
            || publication.Payload.HostToolsMarkerPairReset?.ManagedFile is not
            {
                Phase: FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
            })
        {

            return FullAdmissionAccepted(admitted);

        }

        InstallationResetActiveRecord active = publication.Payload.ToRecord();

        // The installation identity comes from the authenticated claim rather than the anchor beside
        // it. The claim is what the operator's signed statement was bound to, and it is the field
        // every other step of this operation has already been checked against.
        AuthenticatedActiveWriter writer = new(
            activeStore,
            heldInstallationLock,
            activeStore.GuardedRoot,
            claim.InstallationId,
            publication);

        IFullInstallationResetTerminalContinuation terminal = terminalContinuation();

        return await ContinueApplyAsync(
            writer,
            new InstallationResetApplyProgress(active),
            ReproduceAcceptedPlan(active),
            cancellationToken,
            async token =>
            {

                Result<FullInstallationResetTerminalOutcome> completed =
                    await terminal.CompleteAsync(
                        heldInstallationLock,
                        writer.Publication ?? publication,
                        token).ConfigureAwait(false);

                if (completed.IsFailure)
                {

                    return Result<InstallationResetActiveRecord>.Failure(completed.Error);

                }

                // Whatever it published is now the current record, and the next thing this writer does
                // is publish verification on top of it.
                writer.Adopt(completed.Value.Publication);

                return Result<InstallationResetActiveRecord>.Success(
                    completed.Value.Publication.Payload.ToRecord());

            }).ConfigureAwait(false);

    }

    /// <summary>
    /// Hands the authenticated claim to the marker-pair coordinator, and to nothing else.
    /// </summary>
    /// <remarks>
    /// This is the one production caller. It runs after the remediation claim is durable, because
    /// the coordinator authenticates against that published record rather than against anything this
    /// service tells it, and before any later terminalization, because a reset that reported itself
    /// finished while both host-tools markers were still present would be reporting the one thing it
    /// cannot yet prove.
    ///
    /// <para>It passes only the authorization it already holds: the held lock, the publication it
    /// just read or wrote, and the operator's own signed statement. It mints no authority, supplies
    /// no identity of its own, and derives nothing — everything the coordinator acts on it proves
    /// again from the durable record.</para>
    ///
    /// <para>The admission outcome does not depend on the answer, and that is deliberate rather than
    /// an oversight. An externally authorized full reset is incomplete either way — the operator is
    /// told recovery is required whatever happened here — so letting a marker-pair refusal replace an
    /// accepted admission would report the operation as never having been admitted while its claim
    /// sits durable on disk. The progress this call makes is the checkpoint it publishes, and the
    /// next resume reads that rather than a status code.</para>
    /// </remarks>
    private async Task RunMarkerPairResetAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication? publication,
        InstallationResetActiveRecord record,
        FullInstallationResetExternalRemediationAttestation attestation,
        CancellationToken cancellationToken)
    {

        if (markerPairReset is null || publication is null)
        {

            return;

        }

        // Resolved here rather than taken as a constructor dependency. Planning, reporting, and the
        // ordinary reset paths must keep working on an installation whose Grimoire is absent, locked,
        // or unopenable, and the coordinator's graph reaches the Covenant tier and the encrypted
        // database behind it — so binding it at construction would make every one of those paths
        // require a database they were specifically built not to need.
        IHostToolsMarkerPairResetCoordinator coordinator = markerPairReset();

        // A record already carrying a pair checkpoint is resumed rather than begun. Beginning would
        // refuse it anyway — the coordinator admits a begin only for a record that has no checkpoint
        // at all — but asking for the wrong one would turn a resumable operation into a refusal that
        // reads like corruption.
        _ = record.HostToolsMarkerPairReset is null
            ? await coordinator.BeginAsync(
                heldInstallationLock,
                publication,
                attestation,
                cancellationToken).ConfigureAwait(false)
            : await coordinator.ResumeAsync(
                heldInstallationLock,
                publication,
                cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<InstallationResetResult>>
        ApplyFreshUnderMaintenanceLockAsync(
            InstallationResetPlanRequest request,
            StoppedHostInstallationResetPlan confirmedPlan,
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(confirmedPlan);

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(activeStore.GuardedRoot);

        if (_stoppedHostDataService is null || _stoppedHostPairReader is null)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The stopped-host installation-reset data path is unavailable."));

        }

        if (confirmedPlan.Plan.Scope != request.Scope
            || (request.Scope is InstallationResetScope.Workspace
                && confirmedPlan.CovenantDisclosure is not null)
            || (request.Scope is InstallationResetScope.Global or InstallationResetScope.All
                && confirmedPlan.CovenantDisclosure is null))
        {

            return PlanChanged<InstallationResetResult>();

        }

        StoppedHostGrimoireAuthorityIssuer issuer =
            CreateInstallationResetStoppedHostIssuer(heldInstallationLock);

        if (request.Scope is InstallationResetScope.Global or InstallationResetScope.All)
        {

            Result<HostProcessToolsMarkerPairJoinResult> pair =
                await _stoppedHostPairReader.ReadUnderStoppedHostAuthorityAsync(
                    issuer,
                    cancellationToken).ConfigureAwait(false);

            if (pair.IsFailure
                || pair.Value.Disposition is not HostProcessToolsMarkerPairDisposition.Clean)
            {

                return ExternalRemediationRequired<InstallationResetResult>();

            }

        }

        Result<Guid> installation = await _stoppedHostDataService
            .ReadIdentityUnderStoppedHostAuthorityAsync(
                issuer,
                cancellationToken).ConfigureAwait(false);

        if (installation.IsFailure || installation.Value == Guid.Empty)
        {

            return Result<InstallationResetResult>.Failure(
                installation.IsFailure
                    ? installation.Error
                    : new Error(
                        ErrorCodes.Data.ControlPathUnavailable,
                        "The installation identity is unavailable."));

        }

        Result<InstallationResetActiveRecoveryState> recovered = await activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(recovered.Error);

        }

        if (recovered.Value.Outcome
                is not InstallationResetActiveRecoveryOutcome.NoActiveRecord
            || recovered.Value.Publication is not null
            || recovered.Value.LegacyRecord is not null)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ResetInProgress,
                "An existing installation reset must resume through its authenticated recovery path."));

        }

        Result<StoppedHostInstallationResetPlan> replanned =
            await PlanUnderStoppedHostLockAsync(
                request,
                issuer,
                cancellationToken).ConfigureAwait(false);

        if (replanned.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(replanned.Error);

        }

        if (!string.Equals(
                confirmedPlan.Plan.PlanId,
                replanned.Value.Plan.PlanId,
                StringComparison.Ordinal)
            || !Equals(
                confirmedPlan.CovenantDisclosure,
                replanned.Value.CovenantDisclosure))
        {

            return PlanChanged<InstallationResetResult>();

        }

        InstallationResetPlan plan = replanned.Value.Plan;

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

        AuthenticatedActiveWriter writer = new(
            activeStore,
            heldInstallationLock,
            activeStore.GuardedRoot,
            installation.Value,
            publication: null);

        InstallationResetActiveRecord active = new(
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

        Result published = await writer.WriteAsync(active, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(published.Error);

        }

        InstallationResetApplyProgress progress = new(active);

        try
        {

            return await ContinueApplyAsync(
                writer,
                progress,
                plan,
                cancellationToken,
                stoppedHostIssuer: issuer).ConfigureAwait(false);

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

            Result checkpoint = await writer.WriteAsync(
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

    public async Task<Result<InstallationResetResult>> ApplyUnderMaintenanceLockAsync(
        InstallationResetApplyRequest request,
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        heldInstallationLock.AssertHeldFor(activeStore.GuardedRoot);

        StoppedHostGrimoireAuthorityIssuer issuer = new(
            heldInstallationLock,
            activeStore.GuardedRoot,
            _canonicalDatabasePath);

        if (_stoppedHostDataService is null || _stoppedHostPairReader is null)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The stopped-host installation-reset data path is unavailable."));

        }

        Result<HostProcessToolsMarkerPairJoinResult> pair =
            await _stoppedHostPairReader.ReadUnderStoppedHostAuthorityAsync(
                issuer,
                cancellationToken).ConfigureAwait(false);

        if (pair.IsFailure
            || pair.Value.Disposition is not HostProcessToolsMarkerPairDisposition.Clean)
        {

            return ExternalRemediationRequired<InstallationResetResult>();

        }

        IInstallationResetDatabaseIdentityReader? effectiveIdentityReader =
            identityReader
            ?? activeStore as IInstallationResetDatabaseIdentityReader;

        if (effectiveIdentityReader is null)
        {

            return Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The authenticated installation-reset store is unavailable."));

        }

        Result<Guid> installation = await _stoppedHostDataService
            .ReadIdentityUnderStoppedHostAuthorityAsync(
                issuer,
                cancellationToken).ConfigureAwait(false);

        if (installation.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(installation.Error);

        }

        Result<InstallationResetActiveRecoveryState> recovered = await activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure)
        {

            return Result<InstallationResetResult>.Failure(recovered.Error);

        }

        InstallationResetActivePublication? publication = recovered.Value.Publication;

        InstallationResetActiveRecord? recoveredRecord = publication?.Payload.ToRecord();

        if (recovered.Value.Outcome is InstallationResetActiveRecoveryOutcome.LegacyV1
            && recovered.Value.LegacyRecord is { } legacy
            && recovered.Value.LegacyFileIdentity is { } legacyIdentity)
        {

            Result<InstallationResetActivePublication> migrated = await activeStore
                .MigrateLegacyV1Async(
                    heldInstallationLock,
                    installation.Value,
                    legacy,
                    legacyIdentity,
                    cancellationToken)
                .ConfigureAwait(false);

            if (migrated.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(migrated.Error);

            }

            publication = migrated.Value;

            recoveredRecord = publication.Payload.ToRecord();

        }

        if (recoveredRecord?.FullInstallationResetRemediationClaim is not null)
        {

            return ExternalRemediationRequired<InstallationResetResult>();

        }

        AuthenticatedActiveWriter writer = new(
            activeStore,
            heldInstallationLock,
            activeStore.GuardedRoot,
            installation.Value,
            publication);

        InstallationResetActiveRecord active;

        InstallationResetPlan plan;

        if (recoveredRecord is { } existing)
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

            Result published = await writer.WriteAsync(active, cancellationToken)
                .ConfigureAwait(false);

            if (published.IsFailure)
            {

                return Result<InstallationResetResult>.Failure(published.Error);

            }

        }

        InstallationResetApplyProgress progress = new(active);

        try
        {

            return await ContinueApplyAsync(
                writer,
                progress,
                plan,
                cancellationToken)
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

            Result checkpoint = await writer.WriteAsync(
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

    private StoppedHostGrimoireAuthorityIssuer
        CreateInstallationResetStoppedHostIssuer(
            ArcanumMaintenanceLock heldInstallationLock)
    {

        heldInstallationLock.AssertHeldFor(activeStore.GuardedRoot);

        StoppedHostGrimoireAuthorityIssuer issuer = new(
            heldInstallationLock,
            activeStore.GuardedRoot,
            _canonicalDatabasePath);

        return issuer;

    }

    /// <param name="terminalGate">
    /// An extra step the attested full-reset arm inserts between offline cleanup and verification.
    /// Null for every ordinary reset, which has nothing to do there.
    ///
    /// <para>It returns the record to continue from rather than nothing, because it publishes durable
    /// state of its own. Continuing from the record this method was already holding would write that
    /// state straight back out of existence on the very next checkpoint.</para>
    /// </param>
    private async Task<Result<InstallationResetResult>> ContinueApplyAsync(
        IInstallationResetActiveWriter writer,
        InstallationResetApplyProgress progress,
        InstallationResetPlan plan,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<Result<InstallationResetActiveRecord>>>? terminalGate = null,
        IStoppedHostGrimoireAuthorityIssuer? stoppedHostIssuer = null)
    {

        InstallationResetActiveRecord active = progress.Active;

        if (active.Phase is InstallationResetPhase.Completed)
        {

            return await ReturnCompletedAsync(
                writer,
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

                Result preDataCheckpoint = await writer.WriteAsync(
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

                Result pointOfNoReturnCheckpoint = await writer.WriteAsync(
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

                    DataRetentionApplyRequest dataApplyRequest = new(
                        dataRequest,
                        dataPlanId);

                    Result<DataRetentionApplyResult> applied =
                        stoppedHostIssuer is null
                            ? await dataService.ApplyAsync(
                                dataApplyRequest,
                                cancellationToken).ConfigureAwait(false)
                            : await _stoppedHostDataService!
                                .ApplyUnderStoppedHostAuthorityAsync(
                                    dataApplyRequest,
                                    stoppedHostIssuer,
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

                            Result recoveryCheckpoint = await writer.WriteAsync(
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

                        Result retired = await writer.RetireAsync(
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

            Result dataCheckpoint = await writer.WriteAsync(
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

                Result failureCheckpoint = await writer.WriteAsync(
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

                Result verificationCheckpoint = await writer.WriteAsync(
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

            Result cleanupCheckpoint = await writer.WriteAsync(
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

                Result credentialCheckpoint = await writer.WriteAsync(
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

            // The attested arm's last authorized effect, and it goes exactly here. The Grimoire is
            // gone and the ordinary accepted credentials — including the Campaign root-identity key —
            // are removed, which is the earliest point at which the three restore credentials may be
            // taken; and it is before Verified, which is the latest point at which the installation
            // may still be reported as needing recovery.
            if (terminalGate is not null)
            {

                Result<InstallationResetActiveRecord> terminal =
                    await terminalGate(cancellationToken).ConfigureAwait(false);

                if (terminal.IsFailure)
                {

                    active = active with { LastErrorCode = terminal.Error.Code };

                    progress.Active = active;

                    Result terminalCheckpoint = await writer.WriteAsync(
                        active,
                        cancellationToken).ConfigureAwait(false);

                    return terminalCheckpoint.IsFailure
                        ? Resumable(active, terminalCheckpoint.Error)
                        : Resumable(active, terminal.Error);

                }

                // Continue from what it published, not from what this method remembered. The step
                // records each irreversible credential removal durably, and carrying the older record
                // forward would erase that record on the next checkpoint — leaving an installation
                // whose credentials are gone and whose evidence says they never were.
                active = terminal.Value with
                {
                    Phase = active.Phase,
                    PointOfNoReturn = active.PointOfNoReturn,
                    RowsDeleted = active.RowsDeleted,
                    FilesDeleted = active.FilesDeleted,
                    EstimatedBytesDeleted = active.EstimatedBytesDeleted,
                    CredentialResults = active.CredentialResults,
                    LastErrorCode = active.LastErrorCode,
                };

                progress.Active = active;

            }

            active = active with { Phase = InstallationResetPhase.Verified };

            progress.Active = active;

            Result verifiedCheckpoint = await writer.WriteAsync(
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

            Result completedCheckpoint = await writer.WriteAsync(
                active,
                cancellationToken).ConfigureAwait(false);

            if (completedCheckpoint.IsFailure)
            {

                return Resumable(active, completedCheckpoint.Error);

            }

        }

        return await ReturnCompletedAsync(
            writer,
            active,
            plan,
            cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<Result<InstallationResetPlan>> ReplanFullAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken)
    {

        Result<InstallationResetPlan> local = await PlanAsync(
            request.Request,
            cancellationToken).ConfigureAwait(false);

        if (local.IsFailure)
        {

            return local;

        }

        if (string.Equals(
            request.ExpectedPlanId,
            local.Value.PlanId,
            StringComparison.Ordinal))
        {

            return local;

        }

        if (!_onlineDataPlans.TryGetValue(
                request.ExpectedPlanId,
                out DataRetentionPlan? online))
        {

            return PlanChanged<InstallationResetPlan>();

        }

        Result<InstallationResetPlan> rebound = BindOnlineDataPlan(
            request.Request,
            local.Value,
            online);

        return rebound.IsSuccess
            && string.Equals(
                request.ExpectedPlanId,
                rebound.Value.PlanId,
                StringComparison.Ordinal)
            ? rebound
            : PlanChanged<InstallationResetPlan>();

    }

    private static Result ValidateFullRequest(
        FullInstallationResetRequest? request)
    {

        if (request is null
            || request.ExternalRemediation is null
            || request.OperationId == Guid.Empty
            || request.OperationId != request.ExternalRemediation.OperationId)
        {

            return ExternalRemediationInvalid();

        }

        if (request.Apply is null
            || request.Apply.Request is null
            || request.Apply.Request.Scope is not InstallationResetScope.All
            || string.IsNullOrWhiteSpace(request.Apply.ExpectedPlanId))
        {

            return ExternalRemediationInvalid();

        }

        return Result.Success();

    }

    private static FullInstallationResetRemediationClaimV1 Claim(
        FullInstallationResetRemediationAuthorization authorization) =>
        new(
            Version: 1,
            authorization.OperationId,
            authorization.InstallationId,
            authorization.AttestationDigest,
            authorization.NonceDigest,
            authorization.IssuerDigest,
            authorization.AcceptedAtUtc);

    private static bool SameFullRequest(
        InstallationResetActiveRecord active,
        FullInstallationResetRequest request) =>
        active.OperationId == request.OperationId
        && active.Scope is InstallationResetScope.All
        && active.Phase is InstallationResetPhase.Prepared
        && !active.PointOfNoReturn
        && active.RowsDeleted == 0
        && active.FilesDeleted == 0
        && active.EstimatedBytesDeleted == 0
        && active.CredentialResults.Length == 0
        && active.DataHandoff is null
        && active.OnlineDataCompletion is null
        && active.LastErrorCode == ErrorCodes.Data.RecoveryRequired
        && string.Equals(
            active.PlanId,
            request.Apply.ExpectedPlanId,
            StringComparison.Ordinal);

    private static Result<InstallationResetResult> FullAdmissionAccepted(
        InstallationResetActiveRecord active) =>
        Result<InstallationResetResult>.Success(BuildResult(
            active with { LastErrorCode = ErrorCodes.Data.RecoveryRequired },
            active.AcceptedBinding.PreservedBackups,
            new InstallationResetVerification(
                false,
                [
                    new InstallationResetIssueSummary(
                        ErrorCodes.Data.RecoveryRequired,
                        "The externally authorized full installation reset requires recovery."),
                ]),
            resumeRequired: true));

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
        IInstallationResetActiveWriter writer,
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

            Result verificationCheckpoint = await writer.WriteAsync(
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

        Result retired = await writer.RetireAsync(
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

    private async Task<Result<HostProcessToolsMatchedPair>>
        ReadCurrentTaintedMatchedPairAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken)
    {

        if (_stoppedHostPairReader is null)
        {

            return ExternalRemediationRequired<HostProcessToolsMatchedPair>();

        }

        Result<HostProcessToolsMarkerPairJoinResult> pair =
            await _stoppedHostPairReader.ReadUnderStoppedHostAuthorityAsync(
                issuer,
                cancellationToken).ConfigureAwait(false);

        return pair.IsSuccess
            && pair.Value.Disposition
                is HostProcessToolsMarkerPairDisposition.TaintedMatched
            && pair.Value.MatchedPair is { } matched
                ? Result<HostProcessToolsMatchedPair>.Success(matched)
                : ExternalRemediationRequired<HostProcessToolsMatchedPair>();

    }

    private static Result ExternalRemediationInvalid() =>
        Result.Failure(new Error(
            ErrorCodes.Data.ExternalRemediationInvalid,
            "The external remediation attestation could not be verified."));

    private static Result<T> ExternalRemediationInvalid<T>() =>
        Result<T>.Failure(ExternalRemediationInvalid().Error);

    private static Result<T> ExternalRemediationRequired<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.ExternalRemediationRequired,
            "The host-process-tools marker pair requires external remediation."));

    private static Result<T> PlanChanged<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.PlanChanged,
            "The canonical data plan changed after the installation plan was created."));

    private static Result OnlineCompletionMismatch() =>
        Result.Failure(new Error(
            ErrorCodes.Data.ReconciliationFailed,
            "The authenticated host data reset completion proof did not reconcile."));

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
        InstallationResetAcceptedBinding binding)
    {

        using IncrementalHash hash = BeginCanonicalHash(
            "Arcanum.InstallationReset.AcceptedBinding.v2");

        AppendByte(hash, checked((byte)request.Scope));

        AppendStrings(hash, binding.SelectedRoots);

        AppendStrings(hash, binding.ExcludedRoots);

        AppendUInt32(hash, checked((uint)binding.PreservedBackups.Length));

        foreach (InstallationResetPreservedBackup backup in binding.PreservedBackups)
        {

            AppendString(hash, backup.CanonicalPath);

            AppendString(hash, backup.Identity.Value);

            AppendInt64(hash, backup.Identity.Length);

            AppendUInt64(hash, backup.Identity.HardLinkCount);

        }

        AppendStrings(hash, binding.CredentialAccounts);

        AppendStrings(hash, binding.DataPlanIds);

        return CompleteCanonicalHash(hash);

    }

    private static string ComputePlanId(
        InstallationResetPlanRequest request,
        InstallationResetAcceptedBinding binding,
        DataRetentionPlan? data,
        InstallationResetCredentialSummary[] credentials,
        InstallationResetTargetDescriptor[] fileTargets)
    {

        using IncrementalHash hash = BeginCanonicalHash(
            "Arcanum.InstallationReset.Plan.v2");

        AppendByte(hash, checked((byte)request.Scope));

        AppendString(hash, binding.BindingId);

        AppendByte(hash, data is null ? (byte)0 : (byte)1);

        if (data is not null)
        {

            AppendString(hash, data.PlanId);

            AppendInt64(hash, data.Rows);

            AppendInt64(hash, data.Files);

            AppendInt64(hash, data.EstimatedBytes);

            AppendInt64(hash, data.DerivedRecords);

            AppendByte(hash, data.RequiresConfirmation ? (byte)1 : (byte)0);

        }

        AppendUInt32(hash, checked((uint)credentials.Length));

        foreach (InstallationResetCredentialSummary credential in credentials)
        {

            AppendString(hash, credential.Account);

            AppendByte(hash, checked((byte)credential.Status));

            AppendNullableString(hash, credential.ErrorCode);

        }

        InstallationResetTargetDescriptor[] orderedTargets =
        [
            .. fileTargets
                .OrderBy(static target => target.ResourceId, StringComparer.Ordinal)
                .ThenBy(static target => target.CanonicalPath, StringComparer.Ordinal),
        ];

        AppendUInt32(hash, checked((uint)orderedTargets.Length));

        foreach (InstallationResetTargetDescriptor target in orderedTargets)
        {

            AppendString(hash, target.Category);

            AppendByte(hash, checked((byte)target.Role));

            AppendString(hash, target.ResourceId);

            AppendNullableString(hash, target.CanonicalPath);

            AppendNullableString(hash, target.DatabasePredicate);

            AppendByte(hash, target.Identity is null ? (byte)0 : (byte)1);

            if (target.Identity is { } identity)
            {

                AppendString(hash, identity.Value);

                AppendInt64(hash, identity.Length);

                AppendUInt64(hash, identity.HardLinkCount);

            }

            AppendNullableInt64(hash, target.Rows);

            AppendInt64(hash, target.Files);

            AppendInt64(hash, target.EstimatedBytes);

        }

        return CompleteCanonicalHash(hash);

    }

    private static IncrementalHash BeginCanonicalHash(string domain)
    {

        IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        byte[] domainBytes = Encoding.ASCII.GetBytes(domain);

        try
        {

            hash.AppendData(domainBytes);

            AppendByte(hash, 0);

            return hash;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(domainBytes);

        }

    }

    private static void AppendStrings(IncrementalHash hash, string[] values)
    {

        AppendUInt32(hash, checked((uint)values.Length));

        foreach (string value in values)
        {

            AppendString(hash, value);

        }

    }

    private static void AppendNullableString(IncrementalHash hash, string? value)
    {

        AppendByte(hash, value is null ? (byte)0 : (byte)1);

        if (value is not null)
        {

            AppendString(hash, value);

        }

    }

    private static void AppendString(IncrementalHash hash, string value)
    {

        byte[] bytes = Encoding.UTF8.GetBytes(value);

        try
        {

            AppendUInt32(hash, checked((uint)bytes.Length));

            hash.AppendData(bytes);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(bytes);

        }

    }

    private static void AppendNullableInt64(IncrementalHash hash, long? value)
    {

        AppendByte(hash, value.HasValue ? (byte)1 : (byte)0);

        if (value.HasValue)
        {

            AppendInt64(hash, value.Value);

        }

    }

    private static void AppendByte(IncrementalHash hash, byte value)
    {

        Span<byte> bytes = stackalloc byte[1];

        bytes[0] = value;

        hash.AppendData(bytes);

        CryptographicOperations.ZeroMemory(bytes);

    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {

        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);

        hash.AppendData(bytes);

        CryptographicOperations.ZeroMemory(bytes);

    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {

        Span<byte> bytes = stackalloc byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(bytes, value);

        hash.AppendData(bytes);

        CryptographicOperations.ZeroMemory(bytes);

    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);

        hash.AppendData(bytes);

        CryptographicOperations.ZeroMemory(bytes);

    }

    private static string CompleteCanonicalHash(IncrementalHash hash)
    {

        byte[] digest = hash.GetHashAndReset();

        try
        {

            return Convert.ToHexStringLower(digest);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(digest);

        }

    }

    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed class AuthenticatedActiveWriter(
        IInstallationResetActiveStore store,
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedRoot,
        Guid installationId,
        InstallationResetActivePublication? publication)
        : IInstallationResetActiveWriter
    {

        private InstallationResetActivePublication? _publication = publication;

        public async Task<Result> WriteAsync(
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken)
        {

            heldInstallationLock.AssertHeldFor(guardedRoot);

            Result<InstallationResetActivePublication> written = _publication is null
                ? await store.BeginAsync(
                    heldInstallationLock,
                    installationId,
                    record,
                    cancellationToken).ConfigureAwait(false)
                : await store.AdvanceAsync(
                    heldInstallationLock,
                    _publication,
                    record,
                    cancellationToken).ConfigureAwait(false);

            if (written.IsSuccess)
            {

                _publication = written.Value;

            }

            return written.IsSuccess
                ? Result.Success()
                : Result.Failure(written.Error);

        }

        /// <summary>The publication this writer last made durable, or the one it started from.</summary>
        internal InstallationResetActivePublication? Publication => _publication;

        /// <summary>
        /// Adopts a publication a collaborator made durable under the same held lock.
        /// </summary>
        /// <remarks>
        /// The attested arm's last step publishes through the store directly, because it has to record
        /// each irreversible credential removal before issuing the next one. Every one of those
        /// publications advances the authenticated envelope revision, so a writer still holding the one
        /// it started from would conflict on its very next write — and its very next write is the one
        /// that says the reset is verified. Adopting is how the two stay the same operation rather than
        /// two writers racing for the same record.
        /// </remarks>
        internal void Adopt(InstallationResetActivePublication published)
        {

            ArgumentNullException.ThrowIfNull(published);

            _publication = published;

        }

        public async Task<Result> RetireAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {

            heldInstallationLock.AssertHeldFor(guardedRoot);

            Result retired = await store.RetireAsync(
                heldInstallationLock,
                operationId,
                cancellationToken).ConfigureAwait(false);

            if (retired.IsSuccess)
            {

                _publication = null;

            }

            return retired;

        }

    }

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
