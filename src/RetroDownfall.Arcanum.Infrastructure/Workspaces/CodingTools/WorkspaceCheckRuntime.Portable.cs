using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed partial class WorkspaceCheckRuntime
{
    private static readonly Lock PortableProbeGate = new();

    private static readonly Dictionary<(string Image, string Init), (Task<bool> Task, long Started)> PortableProbes = [];

    internal static WorkspaceCheckExecutionStatus PortableStatus(WorkspaceCheckSettings settings, string workspace)
    {
        if (!settings.Enabled) return new(false, false, "workspace_check is disabled by configuration.");

        var toolchain = WorkspaceCheckPortableToolchain.Resolve(workspace, settings);

        if (!toolchain.Success || !WorkspaceCheckAppleContainerRunner.IsAvailable())
            return new(false, true, $"{ErrorCodes.Guild.ToolBoundaryUnavailable}: {toolchain.Message ?? "The trusted Apple container installation is unavailable."}");

        if (!HasPortableImages(toolchain.ImageReference!, toolchain.InitImageReference!))
            return new(false, true, "The portable compiler requires a running Apple container service and locally installed, pinned Linux ARM64 SDK and init images.");

        return new(true, false, $"Portable .NET {toolchain.SdkVersion} build and test profiles run in a disposable Linux ARM64 guest.");
    }

    private static bool HasPortableImages(string image, string init)
    {
        Task<bool> probe;

        lock (PortableProbeGate)
        {
            foreach (var cached in PortableProbes.ToArray())
            {
                if (!cached.Value.Task.IsCompleted) continue;

                int seconds = cached.Value.Task.IsCompletedSuccessfully && cached.Value.Task.Result ? 30 : 5;

                if (TimeProvider.System.GetElapsedTime(cached.Value.Started) > TimeSpan.FromSeconds(seconds))
                    PortableProbes.Remove(cached.Key);
            }

            if (!PortableProbes.TryGetValue((image, init), out var entry))
            {
                entry = (Task.Run(() => ProbeImagesAsync(image, init)), TimeProvider.System.GetTimestamp());

                PortableProbes.Add((image, init), entry);
            }

            probe = entry.Task;
        }

        // Discovery shares a bounded metadata check. Build, test, and model execution use their own lifetimes.
        return probe.Wait(TimeSpan.FromSeconds(8)) && probe.Result;
    }

    private static async Task<bool> ProbeImagesAsync(string image, string init)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));

        try
        {
            return await new WorkspaceCheckAppleContainerRunner().AreImagesAvailableAsync(image, init, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<WorkspaceCheckToolResultEnvelope> RunPortableAsync(WorkspaceCheckRuntimeRequest request, CancellationToken ct)
    {
        string root = request.WorkspaceRoot;

        string profileId = request.ProfileId;

        bool guestStartAttempted = false;

        try
        {
            var check = GuildOperatorWorkspaceWrite.Call.CurrentCheck(root);

            if (check is null || GuildToolInvocationAmbient.Current is not null || !HasCurrentConfiguration() || !_settings.Enabled
                || check.PhysicalScope.Scopes is not [{ IsDirectory: true, ObjectIdentity: { } rootIdentity }]
                || (await check.ValidateAsync(ct).ConfigureAwait(false)).IsFailure)
                return Outcome("unavailable", ErrorCodes.Guild.ToolBoundaryUnavailable,
                    "A portable workspace check requires a current, authenticated Session and its enrolled workspace.", profileId);

            var toolchain = WorkspaceCheckPortableToolchain.Resolve(root, _settings);

            if (!toolchain.Success || toolchain.FeedIdentity is not { } feedIdentity)
                return Outcome("unavailable", toolchain.Code, toolchain.Message, profileId);

            var resolved = _profiles.Resolve(profileId, request.Options);

            if (resolved.Profile is not { } profile) return Outcome("invalid_request", resolved.Code, resolved.Message, profileId);

            if (!TryGetPortableOptions(profile, out string? configuration, out string? framework, out string? verbosity))
                return Outcome("invalid_request", "portable_profile_unsupported",
                    "Portable checks support build and test with configuration, framework, and verbosity options.", profileId);

            WorkspaceCheckAppleContainerRunner runner = new();

            if (!WorkspaceCheckAppleContainerRunner.IsAvailable())
                return Outcome("unavailable", "portable_runtime_unavailable", "The trusted Apple container installation is unavailable.", profileId);

            await using var scope = _scopeFactory.CreateAsyncScope();

            var sanctum = scope.ServiceProvider.GetRequiredService<ISanctumGuard>();

            var limits = ApplyMemoryCeiling(await sanctum.GetEffectiveResourceLimitsForWorkspaceAsync(root, ct).ConfigureAwait(false));

            await WorkspaceCheckPortableInputs.RecoverAsync(runner, ct).ConfigureAwait(false);

            using var inputs = await WorkspaceCheckPortableInputs.CreateAsync(root, rootIdentity, toolchain.OfflinePackageFeed!, feedIdentity,
                (long)limits.MaxFileWriteMb * 1024 * 1024, ct).ConfigureAwait(false);

            var target = string.IsNullOrEmpty(profile.TargetRelativePath)
                ? WorkspaceCheckTargetResolver.Resolve(inputs.Source)
                : WorkspaceCheckTargetResolver.Resolve(inputs.Source, profile.TargetRelativePath);

            if (!target.Success || target.TargetPath is null)
                return Outcome("invalid_request", target.Code ?? "workspace_target_missing", target.Message ?? "A project or solution is required.", profileId);

            if (!HasCurrentConfiguration() || WorkspaceCheckPortableToolchain.Resolve(root, _settings) != toolchain
                || (await check.ValidateAsync(ct).ConfigureAwait(false)).IsFailure || GuildOperatorWorkspaceWrite.Call.CurrentCheck(root) != check)
                return Outcome("unavailable", ErrorCodes.Guild.ToolBoundaryUnavailable,
                    "The Session, workspace, or approved portable SDK changed during staging.", profileId);

            ct.ThrowIfCancellationRequested();

            var run = await runner.RunAsync(toolchain.ImageReference!, toolchain.InitImageReference!, inputs.Source, inputs.Packages,
                Path.GetRelativePath(inputs.Source, target.TargetPath), profile.Kind, configuration, framework, limits.MaxProcessMemoryMb,
                ArcanumSettingClamps.WorkspaceCheckMaxOutputBytes(_settings.MaxOutputBytes), ct,
                expectedSdkVersion: toolchain.SdkVersion!, verbosity: verbosity, limits: limits).ConfigureAwait(false);

            guestStartAttempted = run.GuestStartAttempted;

            if (!run.CleanupVerified)
            {
                inputs.RetainForRecovery();

                return Outcome("unavailable", "portable_cleanup_pending",
                    "The guest shutdown could not be verified. Its private staging directory is retained for recovery.", profileId);
            }

            ct.ThrowIfCancellationRequested();

            if ((await check.ValidateAsync(ct).ConfigureAwait(false)).IsFailure)
                return Outcome("unavailable", ErrorCodes.Guild.ToolBoundaryUnavailable,
                    "Workspace authority expired before compiler diagnostics could be delivered.", profileId);

            ct.ThrowIfCancellationRequested();

            return BuildRunResult(profileId, toolchain.SdkVersion!, profile.Parser, WorkspaceCheckAppleContainerRunner.GuestWorkspaceRoot,
                null, run.ProcessResult, limits);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Before launch there is no guest to settle. Once a guest could have run, the runner
            // returns cleanup evidence even on cancellation. An unresolved result returns above.
            return guestStartAttempted
                ? Outcome("canceled", "workspace_check_canceled", "The workspace check was canceled.", profileId)
                : Outcome("canceled", "workspace_check_canceled_before_start",
                    "The workspace check was canceled before a guest could start.", profileId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Outcome("unavailable", "portable_input_unavailable", "Could not stage or reconcile portable compiler inputs. " + ex.Message, profileId);
        }
    }

    internal static bool TryGetPortableOptions(WorkspaceCheckResolvedProfile profile,
        out string? configuration, out string? framework, out string? verbosity)
    {
        configuration = framework = verbosity = null;

        string? verb = profile.Kind switch { WorkspaceCheckKind.Build => "build", WorkspaceCheckKind.Test => "test", _ => null };

        string[] arguments = profile.Arguments;

        if (verb is null || arguments.Length == 0 || arguments[0] != verb) return false;

        for (int i = 1; i < arguments.Length; i++)
        {
            string option = arguments[i];

            if (option == "--no-restore") continue;

            if (++i == arguments.Length) return false;

            switch (option)
            {
                case "-c" or "--configuration" when configuration is null: configuration = arguments[i]; break;
                case "-f" or "--framework" when framework is null: framework = arguments[i]; break;
                case "-v" or "--verbosity" when verbosity is null: verbosity = arguments[i]; break;
                default: return false;
            }
        }

        return configuration is null or "Debug" or "Release"
            && verbosity is null or "quiet" or "minimal" or "normal" or "detailed" or "diagnostic"
            && (framework is null || WorkspaceCheckAppleContainerRunner.IsSafeFramework(framework));
    }
}
