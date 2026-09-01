using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class InstallationResetApplyBoundaryTests
{

    [Fact]

    public async Task Full_apply_rejects_operation_mismatch_before_shutdown_lock_or_service()
    {

        FullInstallationResetRequest request = CreateFullRequest() with
        {
            OperationId = Guid.Parse("71717171-7171-4171-8171-717171717171"),
        };

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException("Ordinary apply must not run."));

        InstallationResetApplyBoundary boundary = new(
            _ => throw new InvalidOperationException("Shutdown must not run."),
            (_, _) => throw new InvalidOperationException("Host reset must not run."),
            service,
            _ => throw new InvalidOperationException("Lock acquisition must not run."),
            new ImmediateTimeProvider(),
            (_, _, _, _) => throw new InvalidOperationException(
                "Client coordination must not run."),
            (_, _) => throw new InvalidOperationException(
                "Host handoff must not be created."),
            _ => throw new InvalidOperationException(
                "Ordinary pair evidence must not be read."));

        Result<InstallationResetResult> result = await boundary.ApplyFullAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, result.Error.Code);

        Assert.Equal(0, service.ApplyCount);

    }

    [Fact]

    public async Task Full_apply_rejects_a_non_all_scope_before_shutdown_lock_or_service()
    {

        FullInstallationResetRequest original = CreateFullRequest();

        FullInstallationResetRequest request = original with
        {
            Apply = original.Apply with
            {
                Request = original.Apply.Request with
                {
                    Scope = InstallationResetScope.Global,
                },
            },
        };

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException("Ordinary apply must not run."));

        InstallationResetApplyBoundary boundary = new(
            _ => throw new InvalidOperationException("Shutdown must not run."),
            (_, _) => throw new InvalidOperationException("Host reset must not run."),
            service,
            _ => throw new InvalidOperationException("Lock acquisition must not run."),
            new ImmediateTimeProvider(),
            (_, _, _, _) => throw new InvalidOperationException(
                "Client coordination must not run."),
            (_, _) => throw new InvalidOperationException(
                "Host handoff must not be created."),
            _ => throw new InvalidOperationException(
                "Ordinary pair evidence must not be read."));

        Result<InstallationResetResult> result = await boundary.ApplyFullAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ExternalRemediationInvalid, result.Error.Code);

        Assert.Equal(0, service.ApplyCount);

        Assert.Equal(0, service.FullApplyCount);

    }

    [Fact]

    public async Task Full_apply_stops_the_host_and_calls_only_the_full_service_under_the_exact_lock()
    {

        List<string> events = [];

        FullInstallationResetRequest request = CreateFullRequest();

        RecordingLease lease = new();

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException("Ordinary apply must not run."))
        {
            FullApply = (actual, heldInstallationLock, _) =>
            {

                events.Add("full");

                Assert.Equal(request, actual);

                Assert.Same(lease.MaintenanceLock, heldInstallationLock);

                Assert.False(lease.IsDisposed);

                return Task.FromResult(
                    Result<InstallationResetResult>.Success(
                        CreateResult(actual.Apply)));

            },
        };

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => throw new InvalidOperationException(
                "The ordinary host reset must not run."),
            service,
            _ =>
            {

                events.Add("lock");

                return Acquired(lease);

            },
            new ImmediateTimeProvider(),
            (_, _, _, _) => throw new InvalidOperationException(
                "Client coordination must not run."),
            (_, _) => throw new InvalidOperationException(
                "Host handoff must not be created."),
            _ => throw new InvalidOperationException(
                "Ordinary pair evidence must not be read."));

        Result<InstallationResetResult> result = await boundary.ApplyFullAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(["quit", "lock", "full"], events);

        Assert.Equal(0, service.ApplyCount);

        Assert.Equal(1, service.FullApplyCount);

    }

    [Fact]
    public async Task Fresh_global_apply_skips_online_handoff_and_keeps_coordination_through_local_apply()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        RecordingResetService service = new((request, _) =>
        {

            events.Add("offline");

            return Task.FromResult(
                Result<InstallationResetResult>.Success(CreateResult(request)));

        });

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => throw new InvalidOperationException(
                "Fresh local apply must not call the host factory route."),
            service,
            _ => Acquired(new RecordingLease()),
            new ImmediateTimeProvider(),
            (scope, planId, operationId, _) =>
            {

                events.Add(operationId is null ? "coordinate-online" : "coordinate-offline");

                return Task.FromResult(
                    Result<IInstallationResetClientCoordinationLease>.Success(
                        new RecordingClientCoordinationLease(events)));

            },
            (_, _) => throw new InvalidOperationException(
                "Fresh local apply must not create a host handoff."),
            _ => throw new InvalidOperationException(
                "Fresh local apply must not read the pair before the lock."));

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            CreateStoppedPlan(plan),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            [
                "quit",
                "coordinate-online",
                "offline",
                "remove-client-blocker",
                "release-client-mutation",
            ],
            events);

    }

    [Fact]
    public async Task Fresh_global_apply_reads_no_pair_or_factory_seam_before_the_exact_lock()
    {

        int shutdownCalls = 0;

        int factoryCalls = 0;

        int coordinationCalls = 0;

        int pairCalls = 0;

        RecordingResetService service = new((request, _) =>
            Task.FromResult(
                Result<InstallationResetResult>.Success(CreateResult(request))));

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                shutdownCalls++;

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) =>
            {

                factoryCalls++;

                return Task.FromResult(
                    Result<DataRetentionApplyResult>.Failure(new Error(
                        "Test.FactoryMustNotRun",
                        "The online factory effect must not run.")));

            },
            service,
            _ => Acquired(new RecordingLease()),
            new ImmediateTimeProvider(),
            (_, _, _, _) =>
            {

                coordinationCalls++;

                return Task.FromResult(
                    Result<IInstallationResetClientCoordinationLease>.Success(
                        new SilentClientCoordinationLease()));

            },
            (_, _) => throw new InvalidOperationException(
                "Fresh local apply must not create a host handoff."),
            _ =>
            {

                pairCalls++;

                throw new InvalidOperationException(
                    "Fresh local apply must not read the pair before the lock.");

            });

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            CreateStoppedPlan(CreatePlan(InstallationResetScope.Global)),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(0, factoryCalls);

        Assert.Equal(1, shutdownCalls);

        Assert.Equal(1, coordinationCalls);

        Assert.Equal(0, pairCalls);

        Assert.Equal(1, service.FreshApplyCount);

    }

    [Fact]
    public async Task Fresh_global_plan_change_removes_the_client_blocker_without_offline_effect()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => throw new InvalidOperationException(
                "Fresh local apply must not call the host factory route."),
            new RecordingResetService((_, _) => Task.FromResult(
                Result<InstallationResetResult>.Failure(new Error(
                    ErrorCodes.Data.PlanChanged,
                    "changed")))),
            _ => Acquired(new RecordingLease()),
            new ImmediateTimeProvider(),
            (_, _, _, _) =>
            {

                events.Add("coordinate");

                return Task.FromResult(
                    Result<IInstallationResetClientCoordinationLease>.Success(
                        new RecordingClientCoordinationLease(events)));

            },
            (_, _) => throw new InvalidOperationException(
                "Fresh local apply must not create a host handoff."),
            _ => throw new InvalidOperationException(
                "Fresh local apply must not read the pair before the lock."));

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            CreateStoppedPlan(plan),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.PlanChanged, result.Error.Code);

        Assert.Equal(
            [
                "quit",
                "coordinate",
                "remove-client-blocker",
                "release-client-mutation",
            ],
            events);

    }

    [Fact]
    public async Task Resumed_durable_completion_skips_host_replay_and_continues_under_the_exact_lock()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetApplyRequest request = new(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            plan.PlanId);

        InstallationResetHostHandoff handoff = CreateTestHostHandoff(
            request,
            plan).Value;

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => throw new InvalidOperationException(
                "A durable host proof must not replay the online effect."),
            new RecordingResetService((_, _) =>
            {

                events.Add("offline");

                return Task.FromResult(
                    Result<InstallationResetResult>.Success(
                        CreateResult(request)));

            }),
            _ => Acquired(new RecordingLease()),
            new ImmediateTimeProvider(),
            (_, _, _, _) => Task.FromResult(
                Result<IInstallationResetClientCoordinationLease>.Success(
                    new RecordingClientCoordinationLease(events))),
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            request,
            handoff,
            onlineCompletionDurable: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            [
                "quit",
                "offline",
                "remove-client-blocker",
                "release-client-mutation",
            ],
            events);

    }

    [Fact]
    public async Task Fresh_workspace_keeps_the_existing_shutdown_lock_offline_sequence()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Workspace);

        RecordingResetService service = new((actual, _) =>
        {

            events.Add("offline-continuation");

            Assert.Equal(plan.PlanId, actual.ExpectedPlanId);

            return Task.FromResult(
                Result<InstallationResetResult>.Success(CreateResult(actual)));

        });

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit-host");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => throw new InvalidOperationException(
                "Workspace reset must not call the host factory route."),
            service,
            _ =>
            {

                events.Add("acquire-maintenance-lock");

                return Acquired(new RecordingLease());

            },
            new ImmediateTimeProvider(),
            SilentClientCoordination,
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Workspace,
                "/workspace"),
            CreateStoppedPlan(plan),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            ["quit-host", "acquire-maintenance-lock", "offline-continuation"],
            events);

    }

    [Fact]
    public async Task Reachable_host_is_shut_down_before_retried_lock_and_apply()
    {

        List<string> events = [];

        ImmediateTimeProvider timeProvider = new();

        RecordingLease lease = new();

        InstallationResetApplyRequest request = CreateRequest();

        InstallationResetResult expected = CreateResult(request);

        RecordingResetService service = new((actual, _) =>
        {

            events.Add("apply");

            Assert.False(lease.IsDisposed);

            Assert.Equal(request, actual);

            return Task.FromResult(Result<InstallationResetResult>.Success(expected));

        });

        string? guardedDirectory = null;

        int lockAttempts = 0;

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => throw new InvalidOperationException(
                "Legacy offline continuation must not call the host factory route."),
            service,
            path =>
            {

                guardedDirectory = path;

                lockAttempts++;

                if (lockAttempts < 3)
                {

                    return InstallationResetMaintenanceLockAttempt.Contended();

                }

                events.Add("lock");

                return Acquired(lease);

            },
            timeProvider,
            SilentClientCoordination,
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Same(expected, result.Value);

        Assert.Equal(ArcanumPaths.GrimoireDirectory, guardedDirectory);

        Assert.Equal(3, lockAttempts);

        Assert.Equal(2, timeProvider.Delays.Count);

        Assert.All(timeProvider.Delays, delay => Assert.True(delay > TimeSpan.Zero));

        Assert.True(timeProvider.Delays[1] >= timeProvider.Delays[0]);

        Assert.Equal(["quit", "lock", "apply"], events);

        Assert.True(lease.IsDisposed);

    }

    [Fact]
    public async Task Unreachable_host_continues_through_the_offline_lock()
    {

        RecordingLease lease = new();

        InstallationResetApplyRequest request = CreateRequest();

        RecordingResetService service = SuccessfulService(request, lease);

        int lockAttempts = 0;

        InstallationResetApplyBoundary boundary = new(
            _ => Task.FromResult(
                Result<bool>.Failure(new Error(
                    ErrorCodes.Connection.Unreachable,
                    "No host is running."))),
            (_, _) => throw new InvalidOperationException(
                "Legacy offline continuation must not call the host factory route."),
            service,
            _ =>
            {

                lockAttempts++;

                return Acquired(lease);

            },
            new ImmediateTimeProvider(),
            SilentClientCoordination,
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, lockAttempts);

        Assert.Equal(1, service.ApplyCount);

        Assert.True(lease.IsDisposed);

    }

    [Fact]
    public async Task Missing_api_key_after_credential_deletion_continues_through_the_offline_lock()
    {

        RecordingLease lease = new();

        InstallationResetApplyRequest request = CreateRequest();

        RecordingResetService service = SuccessfulService(request, lease);

        InstallationResetApplyBoundary boundary = new(
            _ => Task.FromResult(
                Result<bool>.Failure(new Error(
                    ErrorCodes.Security.MissingApiKey,
                    "The master API key is absent."))),
            (_, _) => throw new InvalidOperationException(
                "Legacy offline continuation must not call the host factory route."),
            service,
            _ => Acquired(lease),
            new ImmediateTimeProvider(),
            SilentClientCoordination,
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(1, service.ApplyCount);

        Assert.True(lease.IsDisposed);

    }

    [Fact]
    public async Task Shutdown_failure_stops_before_lock_acquisition_and_apply()
    {

        Error shutdownError = new(
            ErrorCodes.Auth.Unauthorized,
            "The local host rejected the API key.");

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException("Apply must not run."));

        int lockAttempts = 0;

        InstallationResetApplyBoundary boundary = new(
            _ => Task.FromResult(Result<bool>.Failure(shutdownError)),
            (_, _) => throw new InvalidOperationException(
                "Legacy offline continuation must not call the host factory route."),
            service,
            _ =>
            {

                lockAttempts++;

                return Acquired(new RecordingLease());

            },
            new ImmediateTimeProvider(),
            SilentClientCoordination,
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(shutdownError, result.Error);

        Assert.Equal(0, lockAttempts);

        Assert.Equal(0, service.ApplyCount);

    }

    [Fact]
    public async Task Retry_budget_exhaustion_fails_before_apply()
    {

        ImmediateTimeProvider timeProvider = new();

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException("Apply must not run."));

        int lockAttempts = 0;

        InstallationResetApplyBoundary boundary = new(
            _ => Task.FromResult(Result<bool>.Success(true)),
            (_, _) => throw new InvalidOperationException(
                "Legacy offline continuation must not call the host factory route."),
            service,
            _ =>
            {

                lockAttempts++;

                if (lockAttempts > 64)
                {

                    throw new InvalidOperationException("Lock acquisition was not bounded.");

                }

                return InstallationResetMaintenanceLockAttempt.Contended();

            },
            timeProvider,
            SilentClientCoordination,
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.FileLocked, result.Error.Code);

        Assert.Contains("maintenance lock", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.InRange(lockAttempts, 2, 64);

        Assert.Equal(lockAttempts - 1, timeProvider.Delays.Count);

        Assert.Equal(0, service.ApplyCount);

    }

    [Fact]
    public async Task Unsafe_lock_evidence_fails_once_without_retry_delay_or_offline_apply()
    {

        ImmediateTimeProvider timeProvider = new();

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException("Apply must not run."));

        int lockAttempts = 0;

        InstallationResetApplyBoundary boundary = new(
            _ => Task.FromResult(Result<bool>.Success(true)),
            (_, _) => throw new InvalidOperationException(
                "Legacy offline continuation must not call the host factory route."),
            service,
            _ =>
            {

                lockAttempts++;

                return InstallationResetMaintenanceLockAttempt.Unsafe();

            },
            timeProvider,
            SilentClientCoordination,
            CreateTestHostHandoff,
            ReadCleanPairAsync);

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, result.Error.Code);

        Assert.Equal(1, lockAttempts);

        Assert.Empty(timeProvider.Delays);

        Assert.Equal(0, service.ApplyCount);

    }

    private static InstallationResetMaintenanceLockAttempt Acquired(
        RecordingLease lease) =>
        InstallationResetMaintenanceLockAttempt.Acquired(
            lease.MaintenanceLock);

    private static Task<Result<HostProcessToolsMarkerPairJoinResult>>
        ReadCleanPairAsync(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            Result<HostProcessToolsMarkerPairJoinResult>.Success(
                new HostProcessToolsMarkerPairJoinResult(
                    HostProcessToolsMarkerPairDisposition.Clean,
                    MatchedPair: null)));

    }

    private static Task<Result<IInstallationResetClientCoordinationLease>>
        SilentClientCoordination(
            InstallationResetScope scope,
            string planId,
            Guid? operationId,
            CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            Result<IInstallationResetClientCoordinationLease>.Success(
                new SilentClientCoordinationLease()));

    }

    private sealed class SilentClientCoordinationLease :
        IInstallationResetClientCoordinationLease
    {

        public Task<Result> RemoveBlockerIfSafeAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(Result.Success());

        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class RecordingClientCoordinationLease(List<string> events) :
        IInstallationResetClientCoordinationLease
    {

        public Task<Result> RemoveBlockerIfSafeAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            events.Add("remove-client-blocker");

            return Task.FromResult(Result.Success());

        }

        public ValueTask DisposeAsync()
        {

            events.Add("release-client-mutation");

            return ValueTask.CompletedTask;

        }

    }

    private static RecordingResetService SuccessfulService(
        InstallationResetApplyRequest expectedRequest,
        RecordingLease lease) =>
        new((actual, _) =>
        {

            Assert.Equal(expectedRequest, actual);

            Assert.False(lease.IsDisposed);

            return Task.FromResult(
                Result<InstallationResetResult>.Success(CreateResult(actual)));

        });

    private static InstallationResetPlan CreatePlan(
        InstallationResetScope scope) =>
        new(
            "installation-plan-50",
            scope,
            Workspace: null,
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            DataInventoryAvailable: true,
            CredentialInventoryAvailable: true,
            Targets: [],
            Credentials: [],
            PreservedBackups: [],
            Exclusions: [],
            Blockers: [],
            Rows: 12,
            Files: 3,
            EstimatedBytes: 4_096,
            new InstallationResetAcceptedBinding(
                "binding-50",
                SelectedRoots: [],
                ExcludedRoots: [],
                PreservedBackups: [],
                CredentialAccounts: [],
                DataPlanIds: ["data-plan-50"]));

    private static Result<InstallationResetHostHandoff> CreateTestHostHandoff(
        InstallationResetApplyRequest request,
        InstallationResetPlan plan) =>
        new InstallationResetHostHandoff(
            Guid.Parse("51515151-5151-4151-8151-515151515151"),
            plan.PlanId,
            request.Request.Scope,
            plan.Workspace,
            plan.AcceptedBinding);

    private static StoppedHostInstallationResetPlan CreateStoppedPlan(
        InstallationResetPlan plan) =>
        new(
            plan,
            plan.Scope is InstallationResetScope.Workspace
                ? null
                : new DataRetentionCovenantInventory(
                    Rows: 12,
                    ManagedFiles: 3,
                    LocalArtifacts: 2,
                    AffectedSessions: 1,
                    PossibleDisclosures: 1,
                    RetroDownfall.Arcanum.Core.Covenant.CovenantDisclosureCountKind.Exact));


    private static DataRetentionApplyResult CreateOnlineResult(
        InstallationResetHostHandoff handoff) =>
        new(
            Guid.Parse("52525252-5252-4252-8252-525252525252"),
            Assert.Single(handoff.AcceptedBinding.DataPlanIds),
            RowsDeleted: 12,
            FilesDeleted: 3,
            EstimatedBytesDeleted: 4_096,
            DerivedRecordsDeleted: 2,
            Reconciled: true,
            Blockers: [],
            Conflicts: [],
            RequestedOperationId: handoff.RequestedOperationId);


    private static InstallationResetApplyRequest CreateRequest() =>
        new(
            new InstallationResetPlanRequest(
                InstallationResetScope.Workspace,
                "/workspace"),
            "installation-plan-50");

    private static FullInstallationResetRequest CreateFullRequest()
    {

        Guid operationId = Guid.Parse("70707070-7070-4070-8070-707070707070");

        FullInstallationResetExternalRemediationAttestation attestation = new(
            Version: 1,
            operationId,
            InstallationId: Guid.Parse("72727272-7272-4272-8272-727272727272"),
            HostToolsTransitionId: Guid.Parse("73737373-7373-4373-8373-737373737373"),
            TaintMasterKeyVersion: ulong.MaxValue,
            AuthorityFingerprint: Digest(0x10),
            DatabaseMarkerDigest: Digest(0x20),
            OsMarkerDigest: Digest(0x30),
            RemediationActionDigest: Digest(0x40),
            NonceBase64Url: "AAECAwQFBgcICQoLDA0ODw",
            Issuer: "RetroDownfall.Remediation.v1",
            IssuedAtUtc: new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            ExpiresAtUtc: new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
            SignatureBase64Url: new string('A', 86));

        return new FullInstallationResetRequest(
            operationId,
            new InstallationResetApplyRequest(
                new InstallationResetPlanRequest(
                    InstallationResetScope.All,
                    "/workspace"),
                "installation-plan-50"),
            attestation);

    }

    private static RetroDownfall.Arcanum.Core.Covenant.CovenantDigest Digest(
        byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

    private static InstallationResetResult CreateResult(
        InstallationResetApplyRequest request) =>
        new(
            Guid.Parse("50505050-5050-5050-5050-505050505050"),
            request.ExpectedPlanId,
            request.Request.Scope,
            InstallationResetPhase.Completed,
            PointOfNoReturn: true,
            RowsDeleted: 12,
            FilesDeleted: 3,
            EstimatedBytesDeleted: 4_096,
            CredentialResults: [],
            PreservedBackups: [],
            new InstallationResetVerification(
                Succeeded: true,
                RemainingIssues: []),
            ResumeRequired: false);

    private sealed class RecordingResetService(
        Func<
            InstallationResetApplyRequest,
            CancellationToken,
            Task<Result<InstallationResetResult>>> apply) :
        IInstallationResetService,
        IInstallationResetLockedService
    {

        public int ApplyCount { get; private set; }

        public int FullApplyCount { get; private set; }

        public int FreshApplyCount { get; private set; }

        public Func<
            FullInstallationResetRequest,
            ArcanumMaintenanceLock,
            CancellationToken,
            Task<Result<InstallationResetResult>>>? FullApply { get; init; }

        public Task<Result<InstallationResetPlan>> PlanAsync(
            InstallationResetPlanRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The apply boundary must not plan installation reset state.");

        public Task<Result<InstallationResetResult>> ApplyAsync(
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The test reset service requires the held maintenance lock.")));

        public Task<Result<InstallationResetResult>> ApplyFullAsync(
            FullInstallationResetRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The test reset service requires the held maintenance lock.")));

        public Task<Result<InstallationResetResult>> ApplyUnderMaintenanceLockAsync(
            InstallationResetApplyRequest request,
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            ApplyCount++;

            return apply(request, cancellationToken);

        }

        public Task<Result<InstallationResetResult>> ApplyFreshUnderMaintenanceLockAsync(
            InstallationResetPlanRequest request,
            StoppedHostInstallationResetPlan confirmedPlan,
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            FreshApplyCount++;

            ApplyCount++;

            return apply(
                new InstallationResetApplyRequest(
                    request,
                    confirmedPlan.Plan.PlanId),
                cancellationToken);

        }

        public Task<Result<InstallationResetResult>> ApplyFullUnderMaintenanceLockAsync(
            FullInstallationResetRequest request,
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            ArgumentNullException.ThrowIfNull(heldInstallationLock);

            FullApplyCount++;

            return FullApply is null
                ? throw new InvalidOperationException(
                    "No full installation-reset behavior was configured.")
                : FullApply(request, heldInstallationLock, cancellationToken);

        }

    }


    private sealed class RecordingLease
    {

        private readonly string _guardedDirectory;

        public RecordingLease()
        {

            string parent = Path.Combine(
                Path.GetTempPath(),
                $"arcanum-reset-boundary-{Guid.NewGuid():N}");

            RetroDownfall.Arcanum.Infrastructure.Security.SecureFilePermissions
                .CreateOwnerOnlyDirectoryAtPath(parent);

            _guardedDirectory = Path.Combine(parent, "grimoire");

            ArcanumMaintenanceLockAcquisitionResult acquired =
                ArcanumMaintenanceLock.AcquireDetailed(_guardedDirectory);

            MaintenanceLock = acquired.BorrowAcquiredLock();

        }

        public ArcanumMaintenanceLock MaintenanceLock { get; }

        public bool IsDisposed
        {
            get
            {

                try
                {

                    MaintenanceLock.AssertHeldFor(_guardedDirectory);

                    return false;

                }
                catch (ObjectDisposedException)
                {

                    return true;

                }

            }

        }

    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {

        private long _elapsedTicks;

        public List<TimeSpan> Delays { get; } = [];

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Read(ref _elapsedTicks);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {

            Delays.Add(dueTime);

            Interlocked.Add(ref _elapsedTicks, dueTime.Ticks);

            ThreadPool.QueueUserWorkItem(_ => callback(state));

            return NoopTimer.Instance;

        }

        private sealed class NoopTimer : ITimer
        {

            public static NoopTimer Instance { get; } = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {

            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        }

    }

}
