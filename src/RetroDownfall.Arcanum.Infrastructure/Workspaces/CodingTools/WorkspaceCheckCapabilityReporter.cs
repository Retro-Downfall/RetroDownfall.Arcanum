using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

public sealed class WorkspaceCheckCapabilityReporter
    : IWorkspaceCheckCapabilityReporter
{
    private static readonly TimeSpan DefaultFreshFor =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultAsyncWait =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultProbeTimeout =
        TimeSpan.FromSeconds(8);
    private readonly object _gate = new();
    private readonly IOptionsMonitor<ArcanumSettings> _settings;
    private readonly Func<string?, string> _generationProvider;
    private readonly Func<
        string?,
        CancellationToken,
        Task<WorkspaceCheckCapabilityStatus>> _probe;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _freshFor;
    private readonly TimeSpan _asyncWait;
    private readonly TimeSpan _probeTimeout;
    private CacheEntry? _cache;
    private RefreshEntry? _refresh;

    public WorkspaceCheckCapabilityReporter(
        IOptionsMonitor<ArcanumSettings> settings)
        : this(
            settings,
            generationProvider: null,
            probe: null,
            TimeProvider.System,
            DefaultFreshFor,
            DefaultAsyncWait,
            DefaultProbeTimeout)
    {
    }

    internal WorkspaceCheckCapabilityReporter(
        IOptionsMonitor<ArcanumSettings> settings,
        Func<string?, string>? generationProvider,
        Func<
            string?,
            CancellationToken,
            Task<WorkspaceCheckCapabilityStatus>>? probe,
        TimeProvider timeProvider,
        TimeSpan freshFor,
        TimeSpan asyncWait,
        TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            freshFor,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            asyncWait,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            probeTimeout,
            TimeSpan.Zero);

        _settings = settings;
        _generationProvider =
            generationProvider ?? BuildGeneration;
        _probe = probe ?? ((workspaceRoot, cancellationToken) =>
            Task.Run(
                () => ProbeCurrent(workspaceRoot),
                cancellationToken));
        _timeProvider = timeProvider;
        _freshFor = freshFor;
        _asyncWait = asyncWait;
        _probeTimeout = probeTimeout;
    }

    public bool IsCurrentlyEligible => GetStatus().IsAvailable;

    public WorkspaceCheckCapabilityStatus GetStatus(
        string? workspaceRoot = null)
    {
        string generation;

        try
        {
            generation = _generationProvider(workspaceRoot);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return new WorkspaceCheckCapabilityStatus(
                false,
                true,
                $"Workspace-check capability generation could not be captured: {ex.Message}");
        }

        long now = _timeProvider.GetTimestamp();

        lock (_gate)
        {
            if (_cache is { } current
                && string.Equals(
                    current.Generation,
                    generation,
                    StringComparison.Ordinal))
            {
                TimeSpan age = _timeProvider.GetElapsedTime(
                    current.CompletedTimestamp,
                    now);

                if (age <= _freshFor)
                {
                    return current.Status;
                }

                EnsureRefreshLocked(
                    workspaceRoot,
                    generation);

                return new WorkspaceCheckCapabilityStatus(
                    false,
                    true,
                    $"Workspace-check capability probe is stale ({age.TotalSeconds:F1}s old); refresh is in progress. Last result: {current.Status.Reason}");
            }

            EnsureRefreshLocked(
                workspaceRoot,
                generation);

            return new WorkspaceCheckCapabilityStatus(
                false,
                true,
                _cache is null
                    ? "Workspace-check capability probe refresh is in progress."
                    : "Workspace-check settings or executable generation changed; capability refresh is in progress.");
        }
    }

    public async ValueTask<WorkspaceCheckCapabilityStatus>
        GetStatusAsync(
            string? workspaceRoot = null,
            CancellationToken cancellationToken = default)
    {
        WorkspaceCheckCapabilityStatus immediate =
            GetStatus(workspaceRoot);
        string generation;

        try
        {
            generation = _generationProvider(workspaceRoot);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            return new WorkspaceCheckCapabilityStatus(
                false,
                true,
                $"Workspace-check capability generation could not be captured: {ex.Message}");
        }

        Task<WorkspaceCheckCapabilityStatus>? refreshTask;

        lock (_gate)
        {
            refreshTask = _refresh is { } refresh
                && string.Equals(
                    refresh.Generation,
                    generation,
                    StringComparison.Ordinal)
                    ? refresh.Task
                    : null;
        }

        if (refreshTask is null)
        {
            return GetStatus(workspaceRoot);
        }

        try
        {
            _ = await refreshTask
                .WaitAsync(
                    _asyncWait,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return immediate;
        }

        return GetStatus(workspaceRoot);
    }

    private void EnsureRefreshLocked(
        string? workspaceRoot,
        string generation)
    {
        if (_refresh is { } current
            && string.Equals(
                current.Generation,
                generation,
                StringComparison.Ordinal))
        {
            return;
        }

        TaskCompletionSource<WorkspaceCheckCapabilityStatus>
            completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        _refresh = new RefreshEntry(
            generation,
            completion.Task);
        _ = RefreshAsync(
            workspaceRoot,
            generation,
            completion);
    }

    private async Task RefreshAsync(
        string? workspaceRoot,
        string generation,
        TaskCompletionSource<WorkspaceCheckCapabilityStatus>
            completion)
    {
        WorkspaceCheckCapabilityStatus result;

        try
        {
            using CancellationTokenSource timeout =
                new(_probeTimeout);
            Task<WorkspaceCheckCapabilityStatus> probeTask =
                _probe(
                    workspaceRoot,
                    timeout.Token);
            _ = probeTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            result = await probeTask
                .WaitAsync(
                    _probeTimeout,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new WorkspaceCheckCapabilityStatus(
                false,
                true,
                "Workspace-check capability probe timed out.");
        }
        catch (TimeoutException)
        {
            result = new WorkspaceCheckCapabilityStatus(
                false,
                true,
                "Workspace-check capability probe timed out.");
        }
        catch (Exception ex)
        {
            result = new WorkspaceCheckCapabilityStatus(
                false,
                true,
                $"Workspace-check capability probe failed: {ex.Message}");
        }

        string currentGeneration;

        try
        {
            currentGeneration =
                _generationProvider(workspaceRoot);
        }
        catch
        {
            currentGeneration = string.Empty;
        }

        lock (_gate)
        {
            if (_refresh is { } refresh
                && ReferenceEquals(
                    refresh.Task,
                    completion.Task))
            {
                _refresh = null;
            }

            if (string.Equals(
                    currentGeneration,
                    generation,
                    StringComparison.Ordinal))
            {
                _cache = new CacheEntry(
                    generation,
                    result,
                    _timeProvider.GetTimestamp());
            }
        }

        completion.TrySetResult(result);
    }

    private string BuildGeneration(
        string? workspaceRoot)
    {
        ArcanumSettings current = _settings.CurrentValue;
        WorkspaceCheckSettings check =
            current.ResolveWorkspaceChecks();
        string containmentRoot = ResolveContainmentRoot(
            workspaceRoot,
            current.ResolveDefaultWorkspace());
        WorkspaceCheckExecutableCapture executable =
            WorkspaceCheckExecutableRuntimePolicy
                .ForCurrentPlatform()
                .ResolveConfiguredOrInstalled(
                    check.ExecutableCatalog?.DotNet?.Path,
                    containmentRoot);
        WorkspaceCheckExecutableSnapshot? snapshot =
            executable.Snapshot;

        return string.Join(
            '\u001f',
            InternalCodingToolSettingsFingerprint
                .BuildWorkspaceCheck(check),
            containmentRoot,
            executable.Success,
            snapshot?.CanonicalPath,
            snapshot?.Identity.VolumeId,
            snapshot?.Identity.FileId,
            snapshot?.Length,
            snapshot?.LastWriteUtcTicks);
    }

    private WorkspaceCheckCapabilityStatus ProbeCurrent(
        string? workspaceRoot)
    {
        ArcanumSettings current = _settings.CurrentValue;
        WorkspaceCheckSettings check =
            current.ResolveWorkspaceChecks();
        string platform = WorkspaceCheckExecutionPolicy.DetectPlatform();
        bool jailAvailable =
            WorkspaceCheckExecutionPolicy
                .IsMandatoryJailAvailableForCurrentHost();
        WorkspaceCheckExecutionStatus platformStatus =
            WorkspaceCheckExecutionPolicy.Resolve(
                platform,
                check.Enabled,
                pinnedExecutableValid: true,
                mandatoryJailAvailable: jailAvailable);

        if (!platformStatus.IsEligible)
        {

            return Map(platformStatus);
        }

        string containmentRoot = ResolveContainmentRoot(
            workspaceRoot,
            current.ResolveDefaultWorkspace());
        WorkspaceCheckExecutableRuntimePolicy executablePolicy =
            WorkspaceCheckExecutableRuntimePolicy.ForCurrentPlatform();
        WorkspaceCheckExecutableCapture executable =
            executablePolicy.ResolveConfiguredOrInstalled(
                check.ExecutableCatalog?.DotNet?.Path,
                containmentRoot);
        WorkspaceCheckExecutionStatus executableStatus =
            WorkspaceCheckExecutionPolicy.Resolve(
                platform,
                check.Enabled,
                executable.Success,
                jailAvailable);

        if (!executableStatus.IsEligible
            || executable.Snapshot is null)
        {

            return new WorkspaceCheckCapabilityStatus(
                false,
                check.Enabled,
                executable.Message ?? executableStatus.Reason);
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot)
            && Directory.Exists(workspaceRoot))
        {

            WorkspaceCheckSdkResolution sdk =
                WorkspaceCheckSdkResolver.Resolve(
                    workspaceRoot,
                    executable.Snapshot);

            if (!sdk.Success)
            {

                return new WorkspaceCheckCapabilityStatus(
                    false,
                    true,
                    sdk.Message
                    ?? "The workspace-selected SDK is unavailable or untrusted.");
            }

        }

        if (WorkspaceCheckLaunchChainPolicy.Capture() is null)
        {

            return new WorkspaceCheckCapabilityStatus(
                false,
                true,
                "The workspace-check sandbox or process-group launch chain is unavailable or untrusted.");
        }

        // A child that cannot reach a writable .NET PAL root cannot start at all, so the tool must not
        // be advertised on such a host. Probing here keeps `arcanum doctor`, /api/health, and the tool
        // catalog consistent with what an actual run would do.
        if (OperatingSystem.IsMacOS()
            && !MacOsDotNetIpcRoots.AreAvailable())
        {

            return new WorkspaceCheckCapabilityStatus(
                false,
                true,
                "The shared .NET inter-process roots under /tmp/.dotnet are unavailable, so a sandboxed SDK child cannot start.");
        }

        return Map(executableStatus);
    }

    private static WorkspaceCheckCapabilityStatus Map(
        WorkspaceCheckExecutionStatus status) =>
        new(
            status.IsEligible,
            status.IsHealthDegraded,
            status.Reason);

    private static string ResolveContainmentRoot(
        string? requestedWorkspace,
        string? configuredWorkspace)
    {

        if (!string.IsNullOrWhiteSpace(requestedWorkspace)
            && Directory.Exists(requestedWorkspace))
        {

            return Path.GetFullPath(requestedWorkspace);
        }

        if (!string.IsNullOrWhiteSpace(configuredWorkspace)
            && Directory.Exists(configuredWorkspace))
        {

            return Path.GetFullPath(configuredWorkspace);
        }

        return Path.GetFullPath(Path.GetTempPath());
    }

    private sealed record CacheEntry(
        string Generation,
        WorkspaceCheckCapabilityStatus Status,
        long CompletedTimestamp);

    private sealed record RefreshEntry(
        string Generation,
        Task<WorkspaceCheckCapabilityStatus> Task);
}
