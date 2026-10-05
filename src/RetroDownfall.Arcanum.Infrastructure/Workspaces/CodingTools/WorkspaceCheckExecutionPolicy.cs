namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckExecutionStatus(
    bool IsEligible,
    bool IsHealthDegraded,
    string Reason);

/// <summary>
/// Capability gate for <c>workspace_check</c>. This policy is deliberately independent of the
/// arbitrary-command host-process policy: fixed profiles still execute workspace-authored code,
/// and therefore require an active filesystem jail on every invocation.
/// </summary>
internal static class WorkspaceCheckExecutionPolicy
{
    internal const string ExplicitRiskReason =
        "workspace_check is available with mandatory source-read-only Seatbelt isolation. "
        + "Process-group and descendant cleanup are best effort: an intentionally detached descendant may survive "
        + "and use allowed network egress to exfiltrate readable source or package data.";

    internal const string LinuxUnavailableReason =
        "workspace_check is unavailable on Linux because the mandatory filesystem jail is not active in this beta.";

    internal const string WindowsUnavailableReason =
        "workspace_check is unavailable on Windows because no mandatory filesystem jail is available.";

    internal static WorkspaceCheckExecutionStatus Resolve(
        string platform,
        bool enabled,
        bool pinnedExecutableValid,
        bool mandatoryJailAvailable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        if (!enabled)
        {
            return new WorkspaceCheckExecutionStatus(
                IsEligible: false,
                IsHealthDegraded: false,
                "workspace_check is disabled by configuration.");
        }

        if (string.Equals(platform, "Linux", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceCheckExecutionStatus(false, true, LinuxUnavailableReason);
        }

        if (string.Equals(platform, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceCheckExecutionStatus(false, true, WindowsUnavailableReason);
        }

        if (!string.Equals(platform, "macOS", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceCheckExecutionStatus(
                false,
                true,
                "workspace_check is unavailable on this operating system.");
        }

        if (!mandatoryJailAvailable)
        {
            return new WorkspaceCheckExecutionStatus(
                false,
                true,
                "workspace_check is unavailable because the mandatory macOS filesystem jail is not active.");
        }

        if (!pinnedExecutableValid)
        {
            return new WorkspaceCheckExecutionStatus(
                false,
                true,
                "workspace_check is unavailable because the pinned dotnet executable is missing or untrusted.");
        }

        return new WorkspaceCheckExecutionStatus(
            true,
            false,
            ExplicitRiskReason);
    }

    private static readonly MandatoryJailProbeCache s_mandatoryJailProbeCache =
        new(ProbeMandatoryJail);

    internal static bool IsMandatoryJailAvailableForCurrentHost() =>
        s_mandatoryJailProbeCache.IsAvailable();

    private static bool ProbeMandatoryJail()
    {
        if (!OperatingSystem.IsMacOS()
            || !File.Exists("/usr/bin/sandbox-exec"))
        {
            return false;
        }

        return ProbeProcess(
            "/usr/bin/sandbox-exec",
            [
                "-p",
                "(version 1)(allow default)",
                "/usr/bin/true",
            ],
            TimeSpan.FromSeconds(2));
    }

    internal static bool ProbeProcess(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        try
        {
            using System.Diagnostics.Process probe = new()
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            foreach (string argument in arguments)
            {
                probe.StartInfo.ArgumentList.Add(argument);
            }

            if (!probe.Start())
            {
                return false;
            }

            if (!probe.WaitForExit((int)Math.Clamp(
                    timeout.TotalMilliseconds,
                    1,
                    int.MaxValue)))
            {
                try
                {
                    probe.Kill(entireProcessTree: true);
                    _ = probe.WaitForExit(1_000);
                }
                catch (Exception)
                {
                }

                return false;
            }

            return probe.ExitCode == 0;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static string DetectPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        return "Unknown";
    }
}

/// <summary>
/// Caches a healthy mandatory-jail probe result (a <c>sandbox-exec</c> spawn) for the process lifetime.
/// A failed result is never cached: a transient failure (a timeout, a momentary resource shortage) must
/// not report the jail as unavailable until the host restarts, so the next call probes again.
/// </summary>
internal sealed class MandatoryJailProbeCache(Func<bool> probe)
{
    private readonly Lock _gate = new();

    private bool _available;

    internal int ProbeCount { get; private set; }

    internal bool IsAvailable()
    {
        lock (_gate)
        {
            if (_available)
            {
                return true;
            }

            ProbeCount++;

            _available = probe();

            return _available;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _available = false;

            ProbeCount = 0;
        }
    }
}
