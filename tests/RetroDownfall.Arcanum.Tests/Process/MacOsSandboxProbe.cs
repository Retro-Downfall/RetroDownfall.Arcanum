using System.Diagnostics;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Whether this host can apply a Seatbelt profile at all. A nested agent sandbox refuses
/// <c>sandbox-exec</c>, and a test that needs a real jail skips there rather than reading the refusal
/// as a confinement result.
/// </summary>
internal static class MacOsSandboxProbe
{
    internal static bool IsSandboxExecRunnable()
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/sandbox-exec"))
        {
            return false;
        }

        try
        {
            using global::System.Diagnostics.Process probe = new();

            probe.StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/sandbox-exec",

                ArgumentList = { "-p", "(version 1)(allow default)", "/bin/echo", "probe" },

                UseShellExecute = false,

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                CreateNoWindow = true,
            };

            if (!probe.Start())
            {
                return false;
            }

            string error = probe.StandardError.ReadToEnd();

            probe.WaitForExit(5000);

            return probe.ExitCode == 0
                && !error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
