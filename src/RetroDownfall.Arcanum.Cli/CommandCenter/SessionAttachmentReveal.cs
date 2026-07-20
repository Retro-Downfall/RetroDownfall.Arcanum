using System.Diagnostics;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// OS reveal for session attachment files (Finder / Explorer / file manager).
/// Interactive only; no Spectre / Console.WriteLine.
/// </summary>
internal static class SessionAttachmentReveal
{
    public static string TryReveal(string absolutePath, bool isInteractive, out bool started)
    {
        started = false;
        if (!isInteractive)
        {
            return "Reveal is only available in interactive sessions.";
        }

        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return "Reveal: path was empty.";
        }

        OperatingSystemFamily family =
            OperatingSystem.IsWindows() ? OperatingSystemFamily.Windows
            : OperatingSystem.IsMacOS() ? OperatingSystemFamily.MacOS
            : OperatingSystemFamily.Linux;

        if (!TryBuildStartInfo(absolutePath, family, out ProcessStartInfo? startInfo, out string? error)
            || startInfo is null)
        {
            return error ?? "Reveal: unable to build process start info.";
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return $"Reveal: failed to start {startInfo.FileName}.";
            }

            started = true;
            return $"Revealed: {absolutePath}";
        }
        catch (Exception ex)
        {
            return $"Reveal failed: {ex.Message}";
        }
    }

    public static bool TryBuildStartInfo(
        string absolutePath,
        OperatingSystemFamily family,
        out ProcessStartInfo? startInfo,
        out string? error)
    {
        startInfo = null;
        error = null;

        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            error = "Reveal: path was empty.";
            return false;
        }

        switch (family)
        {
            case OperatingSystemFamily.MacOS:
            {
                ProcessStartInfo psi = new()
                {
                    FileName = "open",
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(absolutePath);
                startInfo = psi;
                return true;
            }

            case OperatingSystemFamily.Windows:
            {
                ProcessStartInfo psi = new()
                {
                    FileName = "explorer",
                    UseShellExecute = false,
                };
                // Single ArgumentList entry — no shell string interpolation.
                psi.ArgumentList.Add("/select," + absolutePath);
                startInfo = psi;
                return true;
            }

            case OperatingSystemFamily.Linux:
            {
                string? parent = Path.GetDirectoryName(absolutePath);
                if (string.IsNullOrWhiteSpace(parent))
                {
                    error = "Reveal: parent directory was empty.";
                    return false;
                }

                ProcessStartInfo psi = new()
                {
                    FileName = "xdg-open",
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(parent);
                startInfo = psi;
                return true;
            }

            default:
                error = "Reveal: unsupported platform.";
                return false;
        }
    }
}

internal enum OperatingSystemFamily
{
    MacOS,
    Windows,
    Linux,
}
