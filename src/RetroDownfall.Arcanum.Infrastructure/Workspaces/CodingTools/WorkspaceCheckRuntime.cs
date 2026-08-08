using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckRuntimeRequest(
    string WorkspaceRoot,
    string ProfileId,
    IReadOnlyDictionary<string, string> Options);

internal interface IWorkspaceCheckRuntime
{

    WorkspaceCheckExecutionStatus GetStatus(string workspaceRoot);

    Task<WorkspaceCheckToolResultEnvelope> RunAsync(
        WorkspaceCheckRuntimeRequest request,
        CancellationToken cancellationToken);
}

internal sealed class WorkspaceCheckRuntime : IWorkspaceCheckRuntime
{

    private readonly WorkspaceCheckSettings _settings;

    private readonly WorkspaceCheckProfileCatalog _profiles;

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly ILogger? _logger;

    private readonly WorkspaceCheckExecutableRuntimePolicy _executablePolicy;

    private readonly TimeSpan? _processTimeoutOverride;

    private readonly Func<WorkspaceCheckSettings>? _currentSettingsProvider;

    private readonly string _settingsFingerprint;

    internal WorkspaceCheckRuntime(
        WorkspaceCheckSettings settings,
        IServiceScopeFactory scopeFactory,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        WorkspaceCheckExecutableRuntimePolicy? executablePolicy = null,
        TimeSpan? processTimeoutOverride = null,
        Func<WorkspaceCheckSettings>? currentSettingsProvider = null)
    {

        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _settings = settings;
        _profiles = WorkspaceCheckProfileCatalog.Create(settings);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ = timeProvider;
        _executablePolicy = executablePolicy
            ?? WorkspaceCheckExecutableRuntimePolicy.ForCurrentPlatform();
        _processTimeoutOverride = processTimeoutOverride;
        _currentSettingsProvider = currentSettingsProvider;
        _settingsFingerprint =
            InternalCodingToolSettingsFingerprint
                .BuildWorkspaceCheck(settings);
    }

    public WorkspaceCheckExecutionStatus GetStatus(string workspaceRoot)
    {

        if (_currentSettingsProvider is not null
            && !string.Equals(
                _settingsFingerprint,
                InternalCodingToolSettingsFingerprint
                    .BuildWorkspaceCheck(
                        _currentSettingsProvider()),
                StringComparison.Ordinal))
        {

            return new WorkspaceCheckExecutionStatus(
                false,
                true,
                "workspace_check configuration changed; this stale invocation surface is unavailable.");
        }

        bool jailAvailable =
            WorkspaceCheckExecutionPolicy
                .IsMandatoryJailAvailableForCurrentHost();
        string platform = WorkspaceCheckExecutionPolicy.DetectPlatform();
        WorkspaceCheckExecutionStatus platformStatus =
            WorkspaceCheckExecutionPolicy.Resolve(
                platform,
                _settings.Enabled,
                pinnedExecutableValid: true,
                mandatoryJailAvailable: jailAvailable);

        if (!platformStatus.IsEligible)
        {

            return platformStatus;
        }

        WorkspaceCheckExecutableCapture executable =
            _executablePolicy.ResolveConfiguredOrInstalled(
                _settings.ExecutableCatalog?.DotNet?.Path,
                workspaceRoot);
        WorkspaceCheckExecutionStatus baseline =
            WorkspaceCheckExecutionPolicy.Resolve(
                platform,
                _settings.Enabled,
                executable.Success,
                jailAvailable);

        if (!baseline.IsEligible || executable.Snapshot is null)
        {

            return baseline with
            {
                Reason = executable.Success
                    ? baseline.Reason
                    : executable.Message ?? baseline.Reason,
            };
        }

        WorkspaceCheckSdkResolution sdk = WorkspaceCheckSdkResolver.Resolve(
            workspaceRoot,
            executable.Snapshot);

        return sdk.Success
            && WorkspaceCheckLaunchChainPolicy.Capture() is not null
            ? baseline
            : baseline with
            {
                IsEligible = false,
                IsHealthDegraded = true,
                Reason = sdk.Message
                    ?? "The workspace-selected SDK or trusted launch chain is unavailable.",
            };
    }

    public async Task<WorkspaceCheckToolResultEnvelope> RunAsync(
        WorkspaceCheckRuntimeRequest request,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        WorkspaceCheckExecutionStatus status = GetStatus(
            request.WorkspaceRoot);

        if (!status.IsEligible)
        {

            return Outcome(
                "unavailable",
                "capability_unavailable",
                status.Reason,
                request.ProfileId);
        }

        _logger?.LogWarning(
            "{WorkspaceCheckRisk}",
            WorkspaceCheckExecutionPolicy.ExplicitRiskReason);

        WorkspaceCheckProfileResolution profile = _profiles.Resolve(
            request.ProfileId,
            request.Options);

        if (!profile.Success || profile.Profile is null)
        {

            return Outcome(
                "invalid_request",
                profile.Code,
                profile.Message,
                request.ProfileId);
        }

        string? workspaceTarget;

        {

            WorkspaceCheckTargetResolution target =
                string.IsNullOrWhiteSpace(
                    profile.Profile.TargetRelativePath)
                    ? WorkspaceCheckTargetResolver.Resolve(
                        request.WorkspaceRoot)
                    : WorkspaceCheckTargetResolver.Resolve(
                        request.WorkspaceRoot,
                        profile.Profile.TargetRelativePath);

            if (!target.Success)
            {

                return Outcome(
                    "invalid_request",
                    target.Code,
                    target.Message,
                    request.ProfileId);
            }

            workspaceTarget = target.TargetPath;
        }

        if (profile.Profile.Kind == WorkspaceCheckKind.Lint
            && string.IsNullOrWhiteSpace(workspaceTarget))
        {
            return Outcome(
                "invalid_request",
                "workspace_target_missing",
                "workspace_check found no trusted workspace target.",
                request.ProfileId);
        }

        TimeSpan processTimeout = _processTimeoutOverride
            ?? Timeout.InfiniteTimeSpan;

        WorkspaceCheckExecutableCapture executable =
            _executablePolicy.ResolveConfiguredOrInstalled(
                _settings.ExecutableCatalog?.DotNet?.Path,
                request.WorkspaceRoot);

        if (!executable.Success || executable.Snapshot is null)
        {

            return Outcome(
                "unavailable",
                executable.Code,
                executable.Message,
                request.ProfileId);
        }

        WorkspaceCheckRunDirectories directories;

        try
        {
            directories = WorkspaceCheckRunDirectories.Create();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException)
        {
            return Outcome(
                "unavailable",
                "output_root_unavailable",
                "A server-owned SDK resolver and run root could not be allocated.",
                request.ProfileId);
        }

        WorkspaceCheckSdkResolution sdk = WorkspaceCheckSdkResolver.Resolve(
            request.WorkspaceRoot,
            executable.Snapshot,
            resolverRoot: directories.Root);

        if (!sdk.Success || sdk.Snapshot is null)
        {
            await TryDeleteRunDirectoriesAsync(
                directories).ConfigureAwait(false);

            return Outcome(
                "unavailable",
                sdk.Code,
                sdk.Message,
                request.ProfileId);
        }

        WorkspaceCheckPackageRootResolution packages =
            WorkspaceCheckPackageRootPolicy.Resolve(
                request.WorkspaceRoot);

        if (!packages.Success || packages.Snapshot is null)
        {
            await TryDeleteRunDirectoriesAsync(
                directories).ConfigureAwait(false);

            return Outcome(
                "restore_required",
                packages.Code,
                packages.Message,
                request.ProfileId,
                sdk.Snapshot.Version);
        }

        WorkspaceCheckLaunchChainSnapshot? launchChain =
            WorkspaceCheckLaunchChainPolicy.Capture();

        if (launchChain is null)
        {
            await TryDeleteRunDirectoriesAsync(
                directories).ConfigureAwait(false);

            return Outcome(
                "unavailable",
                "untrusted_launch_chain",
                "The workspace-check sandbox or process-group launch chain is unavailable or not root-owned.",
                request.ProfileId,
                sdk.Snapshot.Version);
        }

        CancellationToken preflightToken = cancellationToken;

        try
        {

            if (directories.SandboxWritableRoots.Any(root =>
                    WorkspaceRootPath.IsWithinOrEqual(
                        root,
                        request.WorkspaceRoot)))
            {

                return Outcome(
                    "unavailable",
                    "output_root_unavailable",
                    "A per-run writable root outside the source workspace could not be allocated.",
                    request.ProfileId,
                    sdk.Snapshot.Version);
            }

            await using AsyncServiceScope scope =
                _scopeFactory.CreateAsyncScope();
            ISanctumGuard sanctum = scope.ServiceProvider
                .GetRequiredService<ISanctumGuard>();
            IProcessResourceLimiter resourceLimiter = scope.ServiceProvider
                .GetRequiredService<IProcessResourceLimiter>();
            ResourceLimits limits = await sanctum
                .GetEffectiveResourceLimitsForWorkspaceAsync(
                    request.WorkspaceRoot,
                    preflightToken)
                .ConfigureAwait(false);
            WorkspaceCheckRestoreSeedOptions seedOptions =
                WorkspaceCheckRestoreSeedOptions.Default with
                {
                    MaxBytes = checked(
                        (long)limits.MaxFileWriteMb
                        * 1024L
                        * 1024L),
                };

            WorkspaceCheckRestoreSeedResult seeded =
                await WorkspaceCheckRestoreArtifactSeeder.SeedAsync(
                    request.WorkspaceRoot,
                    directories.Artifacts,
                    seedOptions,
                    preflightToken).ConfigureAwait(false);

            if (!seeded.Success)
            {

                return Outcome(
                    seeded.Code == "seed_cap_exceeded"
                        ? "failed"
                        : "restore_required",
                    seeded.Code,
                    seeded.Message,
                    request.ProfileId,
                    sdk.Snapshot.Version);
            }

            WorkspaceCheckEnvironmentPaths environment = new(
                executable.Snapshot.DotNetRoot,
                directories.Home,
                directories.CliHome,
                directories.HttpCache,
                directories.Temp,
                packages.Snapshot.CanonicalPath,
                executable.Snapshot.CanonicalPath);
            System.Diagnostics.ProcessStartInfo startInfo =
                WorkspaceCheckProcessStartInfoFactory.Create(
                    executable.Snapshot.CanonicalPath,
                    sdk.Snapshot.SdkEntryPointPath,
                    request.WorkspaceRoot,
                    profile.Profile,
                    directories,
                    environment,
                    workspaceTarget);

            ChildProcessSandboxRequest sandbox =
                ChildProcessSandboxRoots.ForWorkspaceCheck(
                    request.WorkspaceRoot,
                    [
                        packages.Snapshot.CanonicalPath,
                        executable.Snapshot.DotNetRoot,
                        sdk.Snapshot.SdkPath,
                        sdk.Snapshot.RuntimePath,
                    ],
                    directories.SandboxWritableRoots,
                    directories.Root);

            WorkspaceCheckExecutableRevalidation executableRevalidation =
                _executablePolicy.Revalidate(
                    executable.Snapshot,
                    request.WorkspaceRoot);
            WorkspaceCheckSdkRevalidation sdkRevalidation =
                WorkspaceCheckSdkResolver.Revalidate(sdk.Snapshot);
            WorkspaceCheckPackageRootRevalidation packageRevalidation =
                WorkspaceCheckPackageRootPolicy.Revalidate(
                    packages.Snapshot);
            bool restoreInputsValid =
                WorkspaceCheckRestoreArtifactSeeder
                    .RevalidateManifest(
                        request.WorkspaceRoot,
                        seeded.InputManifest!,
                        seedOptions,
                        preflightToken);

            if (!restoreInputsValid)
            {
                return Outcome(
                    "restore_required",
                    "restore_inputs_changed",
                    "A restore-affecting workspace input changed after artifact seeding and before process start.",
                    request.ProfileId,
                    sdk.Snapshot.Version);
            }

            if (!executableRevalidation.Success
                || !sdkRevalidation.Success
                || !packageRevalidation.Success)
            {

                return Outcome(
                    "unavailable",
                    executableRevalidation.Code
                        ?? sdkRevalidation.Code
                        ?? packageRevalidation.Code,
                    executableRevalidation.Message
                        ?? sdkRevalidation.Message
                        ?? packageRevalidation.Message,
                    request.ProfileId,
                    sdk.Snapshot.Version);
            }

            preflightToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            CappedChildProcessRunResult run =
                await CappedChildProcessRunner.RunAsync(
                    startInfo,
                    ChildProcessEnvironmentProfile.WorkspaceCheck,
                    ArcanumSettingClamps.WorkspaceCheckMaxOutputBytes(
                        _settings.MaxOutputBytes),
                    processTimeout,
                    limits,
                    resourceLimiter,
                    cancellationToken,
                    sandbox,
                    _logger,
                    preStartValidation: () =>
                    {

                        WorkspaceCheckExecutableRevalidation executableNow =
                            _executablePolicy.Revalidate(
                                executable.Snapshot,
                                request.WorkspaceRoot);
                        WorkspaceCheckSdkRevalidation sdkNow =
                            WorkspaceCheckSdkResolver.Revalidate(
                                sdk.Snapshot);
                        WorkspaceCheckPackageRootRevalidation packagesNow =
                            WorkspaceCheckPackageRootPolicy.Revalidate(
                                packages.Snapshot);
                        bool restoreInputsNowValid =
                            WorkspaceCheckRestoreArtifactSeeder
                                .RevalidateManifest(
                                    request.WorkspaceRoot,
                                    seeded.InputManifest!,
                                    seedOptions,
                                    CancellationToken.None);
                        bool identitiesValid =
                            executableNow.Success
                            && sdkNow.Success
                            && packagesNow.Success
                            && WorkspaceCheckLaunchChainPolicy.Revalidate(
                                launchChain);

                        return new CappedChildProcessPreStartValidationResult(
                            identitiesValid
                            && restoreInputsNowValid,
                            !restoreInputsNowValid
                                    ? "A restore-affecting workspace input changed immediately before process start."
                                    : executableNow.Message
                                      ?? sdkNow.Message
                                      ?? packagesNow.Message,
                            !restoreInputsNowValid
                                    ? "restore_inputs_changed"
                                    : "trusted_identity_changed");
                    },
                    getCleanupTimeRemaining:
                        static () => TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested
                || run.Outcome is
                    CappedChildProcessOutcome.Canceled
                    or CappedChildProcessOutcome.CanceledBeforeStart
                    or CappedChildProcessOutcome.CanceledWhileReadingOutput)
            {

                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            }

            return BuildRunResult(
                request.ProfileId,
                sdk.Snapshot.Version,
                profile.Profile.Parser,
                request.WorkspaceRoot,
                directories.TestResultsSource!,
                run);
        }
        finally
        {

            if (directories is not null)
            {

                await TryDeleteRunDirectoriesAsync(
                    directories).ConfigureAwait(false);
            }

        }
    }

    private WorkspaceCheckToolResultEnvelope BuildRunResult(
        string profileId,
        string sdkVersion,
        WorkspaceCheckDiagnosticParserKind parser,
        string workspaceRoot,
        WorkspaceCheckTrxSource testResultsSource,
        CappedChildProcessRunResult run)
    {
        WorkspaceCheckDiagnosticParseResult parsed =
            ParseDiagnostics(
                parser,
                run.Stdout.Text,
                run.Stderr.Text,
                workspaceRoot,
                testResultsSource);

        switch (run.Outcome)
        {
            case CappedChildProcessOutcome.TimedOut:
                return new WorkspaceCheckToolResultEnvelope
                {
                    Status = "timed_out",
                    Code = "timed_out",
                    Message =
                        "The workspace check reached its process timeout and the process tree was killed.",
                    ProfileId = profileId,
                    SelectedSdkVersion = sdkVersion,
                    Diagnostics = parsed.Diagnostics,
                    TotalDiagnosticCount =
                        parsed.TotalDiagnosticCount,
                    OmittedDiagnosticCount =
                        parsed.TotalDiagnosticCount
                        - parsed.Diagnostics.Length,
                    ErrorCount = parsed.ErrorCount,
                    WarningCount = parsed.WarningCount,
                    TotalTestCount = parsed.TotalTestCount,
                    PassedTestCount = parsed.PassedTestCount,
                    FailedTestCount = parsed.FailedTestCount,
                    SkippedTestCount = parsed.SkippedTestCount,
                    StandardOutput = run.Stdout.Text,
                    StandardError = run.Stderr.Text,
                    Truncated =
                        parsed.Truncated
                        || run.Stdout.Truncated
                        || run.Stderr.Truncated,
                };
            case CappedChildProcessOutcome.FilesystemSandboxUnavailable:
            case CappedChildProcessOutcome.FilesystemSandboxDeniedByWindowsSanctum:
                return Outcome(
                    "unavailable",
                    "filesystem_jail_unavailable",
                    run.FilesystemSandboxDenialMessage,
                    profileId,
                    sdkVersion);
            case CappedChildProcessOutcome.ResourceLimitExceeded:
                return new WorkspaceCheckToolResultEnvelope
                {
                    Status = "failed",
                    Code = "resource_limit_exceeded",
                    Message =
                        "The workspace check exceeded an OS-enforced resource limit.",
                    ProfileId = profileId,
                    SelectedSdkVersion = sdkVersion,
                    Diagnostics = parsed.Diagnostics,
                    TotalDiagnosticCount =
                        parsed.TotalDiagnosticCount,
                    OmittedDiagnosticCount =
                        parsed.TotalDiagnosticCount
                        - parsed.Diagnostics.Length,
                    ErrorCount = parsed.ErrorCount,
                    WarningCount = parsed.WarningCount,
                    TotalTestCount = parsed.TotalTestCount,
                    PassedTestCount = parsed.PassedTestCount,
                    FailedTestCount = parsed.FailedTestCount,
                    SkippedTestCount = parsed.SkippedTestCount,
                    StandardOutput = run.Stdout.Text,
                    StandardError = run.Stderr.Text,
                    Truncated =
                        parsed.Truncated
                        || run.Stdout.Truncated
                        || run.Stderr.Truncated,
                };
            case CappedChildProcessOutcome.ResourceLimitApplyFailed:
                return new WorkspaceCheckToolResultEnvelope
                {
                    Status = "unavailable",
                    Code = "resource_limit_unavailable",
                    Message =
                        "OS resource limits could not be applied before process start.",
                    ProfileId = profileId,
                    SelectedSdkVersion = sdkVersion,
                    Diagnostics = parsed.Diagnostics,
                    TotalDiagnosticCount =
                        parsed.TotalDiagnosticCount,
                    OmittedDiagnosticCount =
                        parsed.TotalDiagnosticCount
                        - parsed.Diagnostics.Length,
                    ErrorCount = parsed.ErrorCount,
                    WarningCount = parsed.WarningCount,
                    TotalTestCount = parsed.TotalTestCount,
                    PassedTestCount = parsed.PassedTestCount,
                    FailedTestCount = parsed.FailedTestCount,
                    SkippedTestCount = parsed.SkippedTestCount,
                    StandardOutput = run.Stdout.Text,
                    StandardError = run.Stderr.Text,
                    Truncated =
                        parsed.Truncated
                        || run.Stdout.Truncated
                        || run.Stderr.Truncated,
                };
            case CappedChildProcessOutcome.PreStartValidationFailed:
                return Outcome(
                    run.PreStartValidationCode
                        == "restore_inputs_changed"
                            ? "restore_required"
                        : "unavailable",
                    run.PreStartValidationCode
                    ?? "trusted_identity_changed",
                    run.PreStartValidationError
                    ?? "A trusted executable, SDK, runtime, or package-cache identity changed before process start.",
                    profileId,
                    sdkVersion);
            case CappedChildProcessOutcome.Completed:
                break;
            default:
                return Outcome(
                    "failed",
                    "process_failure",
                    "The workspace check process could not complete.",
                    profileId,
                    sdkVersion);
        }

        string? permissionCode = run.ExitCode == 0
            ? null
            : run.SandboxDeniedRoot switch
            {
                ChildProcessSandboxDeniedRootKind.Source =>
                    "source_write_denied",
                ChildProcessSandboxDeniedRootKind.PackageCache =>
                    "package_cache_write_denied",
                ChildProcessSandboxDeniedRootKind.Other =>
                    "permission_denied",
                _ => null,
            };

        return new WorkspaceCheckToolResultEnvelope
        {
            Status = run.ExitCode == 0 ? "ok" : "failed",
            Code = run.ExitCode == 0
                ? null
                : permissionCode
                    ?? "check_failed",
            Message = run.ExitCode == 0
                ? null
                : permissionCode switch
                {
                    "source_write_denied" =>
                        "Workspace-authored code attempted a write denied by the source-read-only jail.",
                    "package_cache_write_denied" =>
                        "Workspace-authored code attempted a write denied by the read-only package cache.",
                    "permission_denied" =>
                        "The workspace check encountered a filesystem permission denial outside its writable run roots.",
                    _ => "The workspace check exited with a nonzero code.",
                },
            ProfileId = profileId,
            SelectedSdkVersion = sdkVersion,
            Diagnostics = parsed.Diagnostics,
            TotalDiagnosticCount = parsed.TotalDiagnosticCount,
            OmittedDiagnosticCount =
                parsed.TotalDiagnosticCount - parsed.Diagnostics.Length,
            ErrorCount = parsed.ErrorCount,
            WarningCount = parsed.WarningCount,
            TotalTestCount = parsed.TotalTestCount,
            PassedTestCount = parsed.PassedTestCount,
            FailedTestCount = parsed.FailedTestCount,
            SkippedTestCount = parsed.SkippedTestCount,
            ExitCode = run.ExitCode,
            StandardOutput = run.Stdout.Text,
            StandardError = run.Stderr.Text,
            Truncated =
                parsed.Truncated
                || run.Stdout.Truncated
                || run.Stderr.Truncated,
        };
    }

    private WorkspaceCheckDiagnosticParseResult ParseDiagnostics(
        WorkspaceCheckDiagnosticParserKind parser,
        string standardOutput,
        string standardError,
        string workspaceRoot,
        WorkspaceCheckTrxSource testResultsSource)
    {
        int maxDiagnostics =
            ArcanumSettingClamps.WorkspaceCheckMaxDiagnostics(
                _settings.MaxDiagnostics);
        WorkspaceCheckDiagnosticParseResult console =
            WorkspaceCheckDiagnosticParser.Parse(
                parser,
                standardOutput,
                standardError,
                workspaceRoot,
                maxDiagnostics,
                includeVsTestFailures:
                    parser
                    != WorkspaceCheckDiagnosticParserKind.VsTest);

        if (parser != WorkspaceCheckDiagnosticParserKind.VsTest)
        {
            return console;
        }

        WorkspaceCheckTrxParseResult trx =
            WorkspaceCheckTrxParser.Parse(
                testResultsSource,
                maxDiagnostics,
                ArcanumSettingClamps.WorkspaceCheckMaxOutputBytes(
                    _settings.MaxOutputBytes));

        if (!trx.ParsedAny)
        {
            return trx.Truncated
                ? console with { Truncated = true }
                : console;
        }

        return WorkspaceCheckDiagnosticParser.MergeAuthoritativeTrx(
            console,
            trx,
            maxDiagnostics);
    }

    private static WorkspaceCheckToolResultEnvelope Outcome(
        string status,
        string? code,
        string? message,
        string? profileId,
        string? sdkVersion = null) =>
        new()
        {
            Status = status,
            Code = code,
            Message = message,
            ProfileId = profileId,
            SelectedSdkVersion = sdkVersion,
        };

    private static async Task TryDeleteRunDirectoriesAsync(
        WorkspaceCheckRunDirectories directories)
    {
        // Only the per-run root is deleted. SharedIpcRoots are host-owned directories shared with the
        // operator's other .NET processes (see MacOsDotNetIpcRoots) and must survive the run.
        await TryDeleteRunRootAsync(
            directories.Root).ConfigureAwait(false);
    }

    private static async Task TryDeleteRunRootAsync(
        string root)
    {
        Task? cleanup = null;

        try
        {
            TimeSpan cleanupTimeout = TimeSpan.FromSeconds(5);

            cleanup = Task.Run(
                () =>
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(
                            root,
                            recursive: true);
                    }
                });

            await cleanup
                .WaitAsync(cleanupTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = cleanup!.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception)
        {
            // Cleanup is bounded and best-effort after the child has been killed.
            // Never widen source or cache access to make cleanup succeed.
        }
    }
}

internal sealed record WorkspaceCheckPackageRootSnapshot(
    string CanonicalPath,
    FileHandleIdentity Identity);

internal sealed record WorkspaceCheckPackageRootResolution(
    bool Success,
    string? Code,
    string? Message,
    WorkspaceCheckPackageRootSnapshot? Snapshot);

internal sealed record WorkspaceCheckPackageRootRevalidation(
    bool Success,
    string? Code,
    string? Message);

internal static class WorkspaceCheckPackageRootPolicy
{

    internal static WorkspaceCheckPackageRootResolution Resolve(
        string workspaceRoot)
    {

        string userProfile = System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.UserProfile);
        string configured = Path.Combine(
            userProfile,
            ".nuget",
            "packages");

        try
        {

            string? canonical = CanonicalizeDirectory(
                configured,
                resolutionDepth: 0);

            if (canonical is null)
            {

                return Failure(
                    "restore_required",
                    "The canonical pre-existing global NuGet package root is unavailable.");
            }

            if (WorkspaceRootPath.IsWithinOrEqual(
                    canonical,
                    Path.GetFullPath(workspaceRoot))
                || !FileHandleIdentityInterop.TryGetPathMetadata(
                    canonical,
                    out FileHandleMetadata metadata))
            {

                return Failure(
                    "untrusted_package_cache",
                    "The global NuGet package root is inside the workspace or its identity is unavailable.");
            }

            return new WorkspaceCheckPackageRootResolution(
                true,
                null,
                null,
                new WorkspaceCheckPackageRootSnapshot(
                    canonical,
                    metadata.Identity));
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            return Failure(
                "restore_required",
                "The canonical pre-existing global NuGet package root could not be resolved.");
        }
    }

    internal static WorkspaceCheckPackageRootRevalidation Revalidate(
        WorkspaceCheckPackageRootSnapshot snapshot)
    {

        if (!Directory.Exists(snapshot.CanonicalPath)
            || !FileHandleIdentityInterop.TryGetPathMetadata(
                snapshot.CanonicalPath,
                out FileHandleMetadata metadata)
            || !FileHandleIdentity.IdentitiesMatch(
                snapshot.Identity,
                metadata.Identity))
        {

            return new WorkspaceCheckPackageRootRevalidation(
                false,
                "package_cache_changed",
                "The global NuGet package root identity changed before process start.");
        }

        return new WorkspaceCheckPackageRootRevalidation(
            true,
            null,
            null);
    }

    private static WorkspaceCheckPackageRootResolution Failure(
        string code,
        string message) =>
        new(false, code, message, null);

    private static string? CanonicalizeDirectory(
        string path,
        int resolutionDepth)
    {

        if (resolutionDepth > 40)
        {

            return null;
        }

        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(root))
        {

            return null;
        }

        string current = root;

        foreach (string component in fullPath[root.Length..].Split(
                     [
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar,
                     ],
                     StringSplitOptions.RemoveEmptyEntries))
        {

            string candidate = Path.Combine(current, component);
            DirectoryInfo directory = new(candidate);

            if (!directory.Exists)
            {

                return null;
            }

            FileSystemInfo? target = directory.ResolveLinkTarget(
                returnFinalTarget: true);
            current = target is null
                ? Path.GetFullPath(candidate)
                : CanonicalizeDirectory(
                    target.FullName,
                    resolutionDepth + 1)
                  ?? string.Empty;

            if (current.Length == 0)
            {

                return null;
            }

        }

        return Path.TrimEndingDirectorySeparator(current);
    }

}
