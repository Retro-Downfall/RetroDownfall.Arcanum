using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Policy and capability probe for the per-invocation Windows AppContainer jail.
/// Native activation is performed by the hidden sandbox broker.
/// </summary>
internal static partial class WindowsAppContainerPolicy
{
    private const int AppContainerNameMax = 64;

    internal static string CreateProfileName()
    {
        string name = "RetroDownfall.Arcanum.Tool." + Guid.NewGuid().ToString("N");
        return name.Length <= AppContainerNameMax ? name : name[..AppContainerNameMax];
    }

    internal static bool IsSafeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return false;
        }

        // Reject alternate data streams while retaining the drive-designator colon.
        int firstColon = path.IndexOf(':');
        if (firstColon != 1 || path.IndexOf(':', firstColon + 1) >= 0)
        {
            return false;
        }

        string[] components = path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(static component => component is "." or ".."))
        {
            return false;
        }

        return path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    internal static bool IsSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            int result = DeriveAppContainerSidFromAppContainerName(
                "RetroDownfall.Arcanum.CapabilityProbe",
                out nint sid);
            if (sid != 0)
            {
                FreeSid(sid);
            }

            // ERROR_NOT_FOUND means the API is present and the probe profile simply does not exist.
            return result is 0 or 1168;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("userenv.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeriveAppContainerSidFromAppContainerName(
        string appContainerName,
        out nint appContainerSid);

    [SupportedOSPlatform("windows")]
    [LibraryImport("advapi32.dll")]
    private static partial nint FreeSid(nint sid);
}
