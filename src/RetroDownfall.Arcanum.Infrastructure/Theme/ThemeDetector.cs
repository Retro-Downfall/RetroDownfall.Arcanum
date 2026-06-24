using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RetroDownfall.Arcanum.Infrastructure.Theme;

[ExcludeFromCodeCoverage] // Reason: OS-specific theme detection (registry/defaults/gsettings); platform-bound.
public sealed class ThemeDetector : IThemeDetector
{

    private bool? _systemPrefersDark;

    public bool SystemPrefersDark => _systemPrefersDark ??= ComputeSystemPrefersDark();

    private static bool ComputeSystemPrefersDark()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                int? light = TryReadAppsUseLightTheme();

                if (light is 1)
                {
                    return false;
                }

                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacOsThemeInterop.TryGetSystemPrefersDark();
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxSystemPrefersDark();
            }
        }
        catch
        {
            return true;
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reading a known DWORD value.")]
    private static int? TryReadAppsUseLightTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            writable: false);

        if (key is null)
        {
            return null;
        }

        object? raw = key.GetValue("AppsUseLightTheme", null, RegistryValueOptions.None);

        if (raw is int i)
        {
            return i;
        }

        return null;
    }

    private static bool LinuxSystemPrefersDark()
    {
        string? gtk = Environment.GetEnvironmentVariable("GTK_THEME");

        if (!string.IsNullOrEmpty(gtk) && gtk.Contains("dark", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? fgbg = Environment.GetEnvironmentVariable("COLORFGBG");

        if (!string.IsNullOrEmpty(fgbg) && fgbg.Contains(';'))
        {
            string[] parts = fgbg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length >= 2
                && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int fg)
                && int.TryParse(parts[^1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int bg))
            {
                if (bg <= 7 && fg > 7)
                {
                    return true;
                }

                if (fg <= 7 && bg > 7)
                {
                    return false;
                }
            }
        }

        return true;
    }

}
