using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class InstallationResetApplyBoundaryTests
{

    [Fact]
    public async Task Fresh_global_apply_persists_host_completion_before_shutdown_and_offline_continuation()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetApplyRequest expectedRequest = new(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            plan.PlanId);

        InstallationResetOnlineDataHandoff handoff = CreateHandoff(plan);

        DataRetentionApplyResult onlineResult = CreateOnlineResult(handoff);

        RecordingOnlineDataHandoff online = new(events)
        {
            PrepareResult = Result<InstallationResetOnlineDataHandoff>.Success(handoff),
            RecordResult = Result.Success(),
        };

        RecordingResetService service = new((actual, _) =>
        {

            events.Add("offline-continuation");

            Assert.Equal(expectedRequest, actual);

            return Task.FromResult(
                Result<InstallationResetResult>.Success(CreateResult(actual)));

        });

        RecordingLease lease = new(() => events.Add("release"));

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit-host");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (request, _) =>
            {

                events.Add("host-factory-apply");

                Assert.Equal("factory-reset", request.Confirmation);

                Assert.Equal(handoff.DataPlanId, request.ExpectedPlanId);

                Assert.Equal(handoff.RequestedOperationId, request.RequestedOperationId);

                return Task.FromResult(
                    Result<DataRetentionApplyResult>.Success(onlineResult));

            },
            service,
            online,
            _ =>
            {

                events.Add("acquire-maintenance-lock");

                return lease;

            },
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            expectedRequest.Request,
            plan,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            [
                "prepare-active",
                "host-factory-apply",
                "record-completion-proof",
                "quit-host",
                "acquire-maintenance-lock",
                "offline-continuation",
                "release",
            ],
            events);

        Assert.Equal(expectedRequest, Assert.Single(online.PrepareRequests));

        Assert.Equal(plan, Assert.Single(online.ConfirmedPlans));

        RecordedCompletion completion = Assert.Single(online.Completions);

        Assert.Equal(handoff, completion.Handoff);

        Assert.Equal(onlineResult, completion.Result);

    }

    [Fact]
    public async Task Host_PlanChanged_retires_only_the_proof_free_handoff_without_shutdown()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetOnlineDataHandoff handoff = CreateHandoff(plan);

        Error changed = new(
            ErrorCodes.Data.PlanChanged,
            "The named host plan changed before effect.");

        RecordingOnlineDataHandoff online = new(events)
        {
            PrepareResult = Result<InstallationResetOnlineDataHandoff>.Success(handoff),
            RetireResult = Result.Success(),
        };

        int shutdownCalls = 0;

        int lockCalls = 0;

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException("Offline continuation must not run."));

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                shutdownCalls++;

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) =>
            {

                events.Add("host-factory-apply");

                return Task.FromResult(
                    Result<DataRetentionApplyResult>.Failure(changed));

            },
            service,
            online,
            _ =>
            {

                lockCalls++;

                return new RecordingLease();

            },
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            plan,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(changed, result.Error);

        Assert.Equal(
            ["prepare-active", "host-factory-apply", "retire-pre-effect"],
            events);

        Assert.Equal(handoff, Assert.Single(online.RetiredHandoffs));

        Assert.Equal(0, shutdownCalls);

        Assert.Equal(0, lockCalls);

        Assert.Equal(0, service.ApplyCount);

    }

    [Theory]

    [InlineData(ErrorCodes.Connection.Unreachable)]

    [InlineData(ErrorCodes.Auth.Unauthorized)]

    [InlineData(ErrorCodes.Data.ReconciliationFailed)]

    public async Task Uncertain_host_failure_preserves_handoff_without_retirement_or_shutdown(
        string errorCode)
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetOnlineDataHandoff handoff = CreateHandoff(plan);

        RecordingOnlineDataHandoff online = new()
        {
            PrepareResult = Result<InstallationResetOnlineDataHandoff>.Success(handoff),
        };

        int shutdownCalls = 0;

        int lockCalls = 0;

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                shutdownCalls++;

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => Task.FromResult(
                Result<DataRetentionApplyResult>.Failure(new Error(
                    errorCode,
                    "The host outcome is not a proven pre-effect plan change."))),
            new RecordingResetService((_, _) =>
                throw new InvalidOperationException("Offline continuation must not run.")),
            online,
            _ =>
            {

                lockCalls++;

                return new RecordingLease();

            },
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            plan,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(errorCode, result.Error.Code);

        Assert.Empty(online.RetiredHandoffs);

        Assert.Equal(0, shutdownCalls);

        Assert.Equal(0, lockCalls);

    }

    [Fact]
    public async Task Completion_proof_failure_preserves_the_handoff_and_live_host()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.All);

        InstallationResetOnlineDataHandoff handoff = CreateHandoff(plan);

        Error proofError = new(
            ErrorCodes.Data.ReconciliationFailed,
            "The completion proof did not reconcile.");

        RecordingOnlineDataHandoff online = new(events)
        {
            PrepareResult = Result<InstallationResetOnlineDataHandoff>.Success(handoff),
            RecordResult = Result.Failure(proofError),
        };

        int shutdownCalls = 0;

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                shutdownCalls++;

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) =>
            {

                events.Add("host-factory-apply");

                return Task.FromResult(
                    Result<DataRetentionApplyResult>.Success(
                        CreateOnlineResult(handoff)));

            },
            new RecordingResetService((_, _) =>
                throw new InvalidOperationException("Offline continuation must not run.")),
            online,
            _ => throw new InvalidOperationException(
                "Maintenance lock must not be acquired before proof durability."),
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.All,
                "/workspace"),
            plan,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(proofError, result.Error);

        Assert.Equal(
            ["prepare-active", "host-factory-apply", "record-completion-proof"],
            events);

        Assert.Equal(0, shutdownCalls);

        Assert.Empty(online.RetiredHandoffs);

    }

    [Theory]

    [InlineData("data-plan")]

    [InlineData("requested-operation")]

    [InlineData("missing-requested-operation")]

    [InlineData("unreconciled")]

    [InlineData("missing-server-operation")]

    [InlineData("requested-server-operation")]

    [InlineData("blocker")]

    [InlineData("conflict")]

    [InlineData("negative-rows")]

    [InlineData("negative-files")]

    [InlineData("negative-estimated-bytes")]

    [InlineData("negative-derived-records")]

    public async Task Malformed_host_completion_is_rejected_by_the_real_proof_authority_and_preserves_replay(
        string dimension)
    {

        ProofAuthorityBoundaryHarness harness = new((handoff, _) =>
            Task.FromResult(Result<DataRetentionApplyResult>.Success(
                MutateOnlineResult(CreateOnlineResult(handoff), handoff, dimension))));

        using CancellationTokenSource cancellation = new();

        Result<InstallationResetResult> result = await harness.Boundary.ApplyAsync(
            harness.Request,
            cancellation.Token);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ReconciliationFailed, result.Error.Code);

        await AssertMatchingReplayPreservedAsync(harness);

    }

    [Fact]
    public async Task Cancellation_during_the_host_call_preserves_replay_without_shutdown_or_retirement()
    {

        using CancellationTokenSource cancellation = new();

        ProofAuthorityBoundaryHarness harness = new((_, hostCancellation) =>
        {

            cancellation.Cancel();

            return Task.FromCanceled<Result<DataRetentionApplyResult>>(
                hostCancellation);

        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Boundary.ApplyAsync(harness.Request, cancellation.Token));

        Assert.Equal(1, harness.Active.ReadAttempts);

        await AssertMatchingReplayPreservedAsync(harness);

    }

    [Fact]
    public async Task Cancellation_while_recording_host_completion_preserves_replay_without_shutdown_or_retirement()
    {

        using CancellationTokenSource cancellation = new();

        ProofAuthorityBoundaryHarness harness = new((handoff, _) =>
            Task.FromResult(Result<DataRetentionApplyResult>.Success(
                CreateOnlineResult(handoff))));

        harness.Active.CancelOnReadAttempt = 2;

        harness.Active.CancellationSource = cancellation;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Boundary.ApplyAsync(harness.Request, cancellation.Token));

        Assert.Equal(2, harness.Active.ReadAttempts);

        await AssertMatchingReplayPreservedAsync(harness);

    }

    [Fact]
    public async Task Resume_with_durable_completion_skips_host_apply_and_continues_offline()
    {

        List<string> events = [];

        InstallationResetApplyRequest request = CreateRequest();

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetOnlineDataHandoff completed = CreateHandoff(plan) with
        {
            DataResetCompleted = true,
        };

        RecordingOnlineDataHandoff online = new(events)
        {
            ReadResult = Result<InstallationResetOnlineDataHandoff?>.Success(completed),
        };

        RecordingResetService service = new((actual, _) =>
        {

            events.Add("offline-continuation");

            Assert.Equal(request, actual);

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
                "Durable completion must replay without a second host erasure."),
            service,
            online,
            _ =>
            {

                events.Add("acquire-maintenance-lock");

                return new RecordingLease();

            },
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            [
                "read-active",
                "quit-host",
                "acquire-maintenance-lock",
                "offline-continuation",
            ],
            events);

        Assert.Empty(online.Completions);

    }

    [Fact]
    public async Task Resume_with_incomplete_handoff_replays_named_host_apply_before_shutdown()
    {

        List<string> events = [];

        InstallationResetApplyRequest request = CreateRequest();

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetOnlineDataHandoff handoff = CreateHandoff(plan);

        RecordingOnlineDataHandoff online = new(events)
        {
            ReadResult = Result<InstallationResetOnlineDataHandoff?>.Success(handoff),
            RecordResult = Result.Success(),
        };

        RecordingResetService service = new((actual, _) =>
        {

            events.Add("offline-continuation");

            Assert.Equal(request, actual);

            return Task.FromResult(
                Result<InstallationResetResult>.Success(CreateResult(actual)));

        });

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                events.Add("quit-host");

                return Task.FromResult(Result<bool>.Success(true));

            },
            (actual, _) =>
            {

                events.Add("host-factory-apply");

                Assert.Equal(handoff.DataPlanId, actual.ExpectedPlanId);

                Assert.Equal(handoff.RequestedOperationId, actual.RequestedOperationId);

                return Task.FromResult(
                    Result<DataRetentionApplyResult>.Success(
                        CreateOnlineResult(handoff)));

            },
            service,
            online,
            _ =>
            {

                events.Add("acquire-maintenance-lock");

                return new RecordingLease();

            },
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            request,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            [
                "read-active",
                "host-factory-apply",
                "record-completion-proof",
                "quit-host",
                "acquire-maintenance-lock",
                "offline-continuation",
            ],
            events);

    }

    [Fact]
    public async Task Global_resume_with_missing_durable_state_fails_before_shutdown_or_offline_apply()
    {

        RecordingOnlineDataHandoff online = new()
        {
            ReadResult = Result<InstallationResetOnlineDataHandoff?>.Success(null),
        };

        int shutdownCalls = 0;

        int lockCalls = 0;

        RecordingResetService service = new((_, _) =>
            throw new InvalidOperationException(
                "Missing durable state must not start the offline factory route."));

        InstallationResetApplyBoundary boundary = new(
            _ =>
            {

                shutdownCalls++;

                return Task.FromResult(Result<bool>.Success(true));

            },
            (_, _) => throw new InvalidOperationException(
                "Missing durable state must not start the host factory route."),
            service,
            online,
            _ =>
            {

                lockCalls++;

                return new RecordingLease();

            },
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyAsync(
            CreateRequest(),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, result.Error.Code);

        Assert.Equal(0, shutdownCalls);

        Assert.Equal(0, lockCalls);

        Assert.Equal(0, service.ApplyCount);

    }

    [Fact]
    public async Task Fresh_workspace_keeps_the_existing_shutdown_lock_offline_sequence()
    {

        List<string> events = [];

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Workspace);

        RecordingOnlineDataHandoff online = new(events);

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
            online,
            _ =>
            {

                events.Add("acquire-maintenance-lock");

                return new RecordingLease();

            },
            new ImmediateTimeProvider());

        Result<InstallationResetResult> result = await boundary.ApplyFreshAsync(
            new InstallationResetPlanRequest(
                InstallationResetScope.Workspace,
                "/workspace"),
            plan,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(
            ["quit-host", "acquire-maintenance-lock", "offline-continuation"],
            events);

        Assert.Empty(online.PrepareRequests);

        Assert.Empty(online.ReadRequests);

    }

    [Fact]
    public async Task Reachable_host_is_shut_down_before_retried_lock_and_apply()
    {

        List<string> events = [];

        ImmediateTimeProvider timeProvider = new();

        RecordingLease lease = new(() => events.Add("release"));

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
            new RecordingOnlineDataHandoff(),
            path =>
            {

                guardedDirectory = path;

                lockAttempts++;

                if (lockAttempts < 3)
                {

                    return null;

                }

                events.Add("lock");

                return lease;

            },
            timeProvider);

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

        Assert.Equal(["quit", "lock", "apply", "release"], events);

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
            new RecordingOnlineDataHandoff(),
            _ =>
            {

                lockAttempts++;

                return lease;

            },
            new ImmediateTimeProvider());

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
            new RecordingOnlineDataHandoff(),
            _ => lease,
            new ImmediateTimeProvider());

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
            new RecordingOnlineDataHandoff(),
            _ =>
            {

                lockAttempts++;

                return new RecordingLease();

            },
            new ImmediateTimeProvider());

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
            new RecordingOnlineDataHandoff(),
            _ =>
            {

                lockAttempts++;

                if (lockAttempts > 64)
                {

                    throw new InvalidOperationException("Lock acquisition was not bounded.");

                }

                return null;

            },
            timeProvider);

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

    private static InstallationResetOnlineDataHandoff CreateHandoff(
        InstallationResetPlan plan) =>
        new(
            Guid.Parse("51515151-5151-4151-8151-515151515151"),
            plan.PlanId,
            Assert.Single(plan.AcceptedBinding.DataPlanIds),
            DataResetCompleted: false);

    private static DataRetentionApplyResult CreateOnlineResult(
        InstallationResetOnlineDataHandoff handoff) =>
        new(
            Guid.Parse("52525252-5252-4252-8252-525252525252"),
            handoff.DataPlanId,
            RowsDeleted: 12,
            FilesDeleted: 3,
            EstimatedBytesDeleted: 4_096,
            DerivedRecordsDeleted: 2,
            Reconciled: true,
            Blockers: [],
            Conflicts: [],
            RequestedOperationId: handoff.RequestedOperationId);

    private static DataRetentionApplyResult MutateOnlineResult(
        DataRetentionApplyResult result,
        InstallationResetOnlineDataHandoff handoff,
        string dimension) =>
        dimension switch
        {
            "data-plan" => result with { PlanId = "different-data-plan" },
            "requested-operation" => result with
            {
                RequestedOperationId = Guid.Parse(
                    "53535353-5353-4353-8353-535353535353"),
            },
            "missing-requested-operation" => result with
            {
                RequestedOperationId = null,
            },
            "unreconciled" => result with { Reconciled = false },
            "missing-server-operation" => result with { OperationId = Guid.Empty },
            "requested-server-operation" => result with
            {
                OperationId = handoff.RequestedOperationId,
            },
            "blocker" => result with
            {
                Blockers =
                [
                    new DataRetentionBlocker(
                        RetentionDataClass.Covenant,
                        "content-free-resource",
                        ErrorCodes.Data.Blocked,
                        "The host reported a blocker."),
                ],
            },
            "conflict" => result with
            {
                Conflicts =
                [
                    new DataRetentionConflict(
                        "proof-conflict",
                        "content-free-resource",
                        "The host reported a conflict."),
                ],
            },
            "negative-rows" => result with { RowsDeleted = -1 },
            "negative-files" => result with { FilesDeleted = -1 },
            "negative-estimated-bytes" => result with
            {
                EstimatedBytesDeleted = -1,
            },
            "negative-derived-records" => result with
            {
                DerivedRecordsDeleted = -1,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        };

    private static InstallationResetActiveRecord CreatePreparedActiveRecord(
        InstallationResetPlan plan,
        InstallationResetOnlineDataHandoff handoff) =>
        new(
            InstallationResetActiveStore.CurrentVersion,
            handoff.RequestedOperationId,
            handoff.InstallationPlanId,
            plan.Scope,
            Workspace: null,
            plan.AcceptedBinding,
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: null,
            DataHandoff: InstallationResetDataHandoff.HostFactoryErasure,
            OnlineDataCompletion: null);

    private static async Task AssertMatchingReplayPreservedAsync(
        ProofAuthorityBoundaryHarness harness)
    {

        Assert.Equal(0, harness.QuitCalls);

        Assert.Equal(0, harness.LockCalls);

        Assert.Equal(0, harness.OfflineService.ApplyCount);

        Assert.Equal(0, harness.Active.RetirementAttempts);

        Assert.Empty(harness.Active.Writes);

        Assert.Equal(harness.PreparedRecord, harness.Active.Record);

        Result<InstallationResetOnlineDataHandoff?> replay =
            await harness.Authority.ReadAsync(
                harness.Request,
                CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(harness.Handoff, replay.Value);

        Assert.False(replay.Value!.DataResetCompleted);

    }

    private static InstallationResetApplyRequest CreateRequest() =>
        new(
            new InstallationResetPlanRequest(
                InstallationResetScope.Global,
                "/workspace"),
            "installation-plan-50");

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
            Task<Result<InstallationResetResult>>> apply) : IInstallationResetService
    {

        public int ApplyCount { get; private set; }

        public Task<Result<InstallationResetPlan>> PlanAsync(
            InstallationResetPlanRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The apply boundary must not plan installation reset state.");

        public Task<Result<InstallationResetResult>> ApplyAsync(
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken = default)
        {

            ApplyCount++;

            return apply(request, cancellationToken);

        }

    }

    private sealed class ProofAuthorityBoundaryHarness
    {

        public ProofAuthorityBoundaryHarness(
            Func<
                InstallationResetOnlineDataHandoff,
                CancellationToken,
                Task<Result<DataRetentionApplyResult>>> applyFactoryReset)
        {

            InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

            Request = new InstallationResetApplyRequest(
                new InstallationResetPlanRequest(
                    InstallationResetScope.Global,
                    "/workspace"),
                plan.PlanId);

            Handoff = CreateHandoff(plan);

            PreparedRecord = CreatePreparedActiveRecord(plan, Handoff);

            Active = new ProofAuthorityActiveStore(PreparedRecord);

            UnusedProofAuthorityDependencies dependencies = new();

            Authority = new InstallationResetService(
                dependencies,
                dependencies,
                Active,
                dependencies);

            OfflineService = new RecordingResetService((request, _) =>
                Task.FromResult(Result<InstallationResetResult>.Success(
                    CreateResult(request))));

            Boundary = new InstallationResetApplyBoundary(
                cancellationToken =>
                {

                    QuitCalls++;

                    cancellationToken.ThrowIfCancellationRequested();

                    return Task.FromResult(Result<bool>.Success(true));

                },
                (request, cancellationToken) =>
                {

                    Assert.Equal("factory-reset", request.Confirmation);

                    Assert.Equal(Handoff.DataPlanId, request.ExpectedPlanId);

                    Assert.Equal(
                        Handoff.RequestedOperationId,
                        request.RequestedOperationId);

                    return applyFactoryReset(Handoff, cancellationToken);

                },
                OfflineService,
                Authority,
                _ =>
                {

                    LockCalls++;

                    return new RecordingLease();

                },
                new ImmediateTimeProvider());

        }

        public InstallationResetApplyBoundary Boundary { get; }

        public InstallationResetService Authority { get; }

        public ProofAuthorityActiveStore Active { get; }

        public InstallationResetApplyRequest Request { get; }

        public InstallationResetOnlineDataHandoff Handoff { get; }

        public InstallationResetActiveRecord PreparedRecord { get; }

        public RecordingResetService OfflineService { get; }

        public int QuitCalls { get; private set; }

        public int LockCalls { get; private set; }

    }

    private sealed class ProofAuthorityActiveStore(
        InstallationResetActiveRecord preparedRecord) : IInstallationResetActiveStore
    {

        public InstallationResetActiveRecord? Record { get; private set; } =
            preparedRecord;

        public List<InstallationResetActiveRecord> Writes { get; } = [];

        public int ReadAttempts { get; private set; }

        public int RetirementAttempts { get; private set; }

        public int? CancelOnReadAttempt { get; set; }

        public CancellationTokenSource? CancellationSource { get; set; }

        public Task<Result<InstallationResetActiveRecord?>> ReadAsync(
            CancellationToken cancellationToken)
        {

            ReadAttempts++;

            if (ReadAttempts == CancelOnReadAttempt)
            {

                CancellationSource!.Cancel();

            }

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<InstallationResetActiveRecord?>.Success(Record));

        }

        public Task<Result> WriteAsync(
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            Writes.Add(record);

            Record = record;

            return Task.FromResult(Result.Success());

        }

        public Task<Result> RetireAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {

            RetirementAttempts++;

            cancellationToken.ThrowIfCancellationRequested();

            Record = null;

            return Task.FromResult(Result.Success());

        }

        public Task<Result> RetirePreEffectAsync(
            InstallationResetOnlineDataHandoff handoff,
            CancellationToken cancellationToken)
        {

            RetirementAttempts++;

            cancellationToken.ThrowIfCancellationRequested();

            Record = null;

            return Task.FromResult(Result.Success());

        }

    }

    private sealed class UnusedProofAuthorityDependencies :
        IInstallationResetDataService,
        IInstallationResetCredentialService,
        IInstallationResetOfflineCleanup
    {

        public Task<Result<DataRetentionPlan>> PlanAsync(
            InstallationResetDataPlanRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Proof validation must not replan host data.");

        public Task<Result<DataRetentionApplyResult>> ApplyAsync(
            DataRetentionApplyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "Proof validation must not apply host data.");

        public InstallationResetCredentialSummary[] Probe() =>
            throw new InvalidOperationException(
                "Proof validation must not inventory credentials.");

        public InstallationResetCredentialResult[] DeleteAndVerify(
            string[] accounts) =>
            throw new InvalidOperationException(
                "Proof validation must not delete credentials.");

        public Task<Result<InstallationResetFileSystemInventory>> PlanAsync(
            string[] selectedRoots,
            string[] excludedRoots,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Proof validation must not inventory offline files.");

        public Task<Result<InstallationResetOfflineCleanupResult>> ExecuteAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Proof validation must not continue offline.");

    }

    private sealed record RecordedCompletion(
        InstallationResetOnlineDataHandoff Handoff,
        DataRetentionApplyResult Result);

    private sealed class RecordingOnlineDataHandoff(
        List<string>? events = null) : IInstallationResetOnlineDataHandoff
    {

        public Result<InstallationResetOnlineDataHandoff> PrepareResult { get; set; } =
            Result<InstallationResetOnlineDataHandoff>.Failure(new Error(
                "Test.PrepareMissing",
                "No online prepare result was configured."));

        public Result<InstallationResetOnlineDataHandoff?> ReadResult { get; set; } =
            Result<InstallationResetOnlineDataHandoff?>.Failure(new Error(
                ErrorCodes.Data.ResetInProgress,
                "This active record uses the legacy offline continuation."));

        public Result RecordResult { get; set; } =
            Result.Failure(new Error(
                "Test.RecordMissing",
                "No completion record result was configured."));

        public Result RetireResult { get; set; } =
            Result.Failure(new Error(
                "Test.RetireMissing",
                "No retirement result was configured."));

        public List<InstallationResetApplyRequest> PrepareRequests { get; } = [];

        public List<InstallationResetPlan> ConfirmedPlans { get; } = [];

        public List<InstallationResetApplyRequest> ReadRequests { get; } = [];

        public List<RecordedCompletion> Completions { get; } = [];

        public List<InstallationResetOnlineDataHandoff> RetiredHandoffs { get; } = [];

        public Result<InstallationResetPlan> BindOnlineDataPlan(
            InstallationResetPlanRequest request,
            InstallationResetPlan localPlan,
            DataRetentionPlan onlinePlan) =>
            throw new InvalidOperationException(
                "The apply boundary must not bind installation plans.");

        public Task<Result<InstallationResetOnlineDataHandoff>> PrepareAsync(
            InstallationResetApplyRequest request,
            InstallationResetPlan confirmedPlan,
            CancellationToken cancellationToken = default)
        {

            events?.Add("prepare-active");

            PrepareRequests.Add(request);

            ConfirmedPlans.Add(confirmedPlan);

            return Task.FromResult(PrepareResult);

        }

        public Task<Result<InstallationResetOnlineDataHandoff?>> ReadAsync(
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken = default)
        {

            events?.Add("read-active");

            ReadRequests.Add(request);

            return Task.FromResult(ReadResult);

        }

        public Task<Result> RecordCompletedAsync(
            InstallationResetOnlineDataHandoff handoff,
            DataRetentionApplyResult result,
            CancellationToken cancellationToken = default)
        {

            events?.Add("record-completion-proof");

            Completions.Add(new RecordedCompletion(handoff, result));

            return Task.FromResult(RecordResult);

        }

        public Task<Result> RetirePreEffectAsync(
            InstallationResetOnlineDataHandoff handoff,
            CancellationToken cancellationToken = default)
        {

            events?.Add("retire-pre-effect");

            RetiredHandoffs.Add(handoff);

            return Task.FromResult(RetireResult);

        }

    }

    private sealed class RecordingLease(Action? onDispose = null) : IDisposable
    {

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {

            IsDisposed = true;

            onDispose?.Invoke();

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
