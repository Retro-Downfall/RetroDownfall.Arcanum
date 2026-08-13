using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class InstallationResetApplyBoundaryTests
{

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
            service,
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
            service,
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
            service,
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
            service,
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
            service,
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
