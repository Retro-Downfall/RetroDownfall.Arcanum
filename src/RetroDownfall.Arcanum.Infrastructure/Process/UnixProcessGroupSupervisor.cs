using System.Diagnostics;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Launches the trusted child as a monitored-shell job in its own Unix process group. The
/// supervisor kills that group after the direct child exits, including the normal-exit path where
/// <see cref="Process.Kill(bool)"/> can no longer discover reparented descendants.
/// </summary>
internal static class UnixProcessGroupSupervisor
{
    private const string SupervisorScript =
        "child=; cleanup() { "
        + "[ -z \"$child\" ] || kill -KILL -\"$child\" 2>/dev/null || :; "
        + "}; trap cleanup EXIT HUP INT TERM; "
        + "set -m; \"$@\" & child=$!; set +m; "
        + "wait \"$child\"; status=$?; "
        + "trap - EXIT; cleanup; "
        + "exit \"$status\"";

    private static readonly string[] LinuxSetSidCandidates =
    [
        "/usr/bin/setsid",
        "/bin/setsid",
    ];

    internal static bool Apply(
        ProcessStartInfo startInfo,
        Func<string?>? linuxSetSidResolver = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        string target = startInfo.FileName;

        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        List<string> arguments = [.. startInfo.ArgumentList];
        string? setSidPath = OperatingSystem.IsLinux()
            ? linuxSetSidResolver is null
                ? LinuxSetSidCandidates.FirstOrDefault(File.Exists)
                : linuxSetSidResolver()
            : null;
        startInfo.ArgumentList.Clear();

        if (setSidPath is not null)
        {
            startInfo.FileName = setSidPath;
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(target);

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return true;
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(SupervisorScript);
        startInfo.ArgumentList.Add("arcanum-process-group");
        startInfo.ArgumentList.Add(target);

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.FileName = "/bin/sh";
        return false;
    }
}
