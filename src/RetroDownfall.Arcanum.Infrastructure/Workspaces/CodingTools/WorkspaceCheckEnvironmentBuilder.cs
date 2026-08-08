namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckEnvironmentPaths(
    string DotNetRoot,
    string Home,
    string DotNetCliHome,
    string NuGetHttpCache,
    string Temp,
    string GlobalPackages,
    string? DotNetHostPath = null);

/// <summary>
/// Constructs the complete environment for a workspace check. Nothing is inherited implicitly;
/// only a short OS/runtime allowlist may be copied from the host.
/// </summary>
internal static class WorkspaceCheckEnvironmentBuilder
{

    private static readonly string[] UnixEssentials =
    [
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "TZ",
        "__CF_USER_TEXT_ENCODING",
        "USER",
        "LOGNAME",
    ];

    private static readonly string[] WindowsEssentials =
    [
        "SystemRoot",
        "WINDIR",
        "COMSPEC",
        "PATHEXT",
        "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE",
    ];

    internal static IReadOnlyDictionary<string, string> Build(
        WorkspaceCheckEnvironmentPaths paths,
        IReadOnlyDictionary<string, string?>? hostEnvironment = null)
    {

        ArgumentNullException.ThrowIfNull(paths);

        Dictionary<string, string> result =
            new(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        IReadOnlyList<string> essentials = OperatingSystem.IsWindows()
            ? WindowsEssentials
            : UnixEssentials;

        foreach (string name in essentials)
        {

            string? value = ReadHostValue(hostEnvironment, name);

            if (IsSafeValue(value))
            {

                result[name] = value!;
            }

        }

        result["DOTNET_ROOT"] = paths.DotNetRoot;
        result["DOTNET_HOST_PATH"] =
            paths.DotNetHostPath
            ?? Path.Combine(
                paths.DotNetRoot,
                OperatingSystem.IsWindows()
                    ? "dotnet.exe"
                    : "dotnet");
        result["PATH"] = BuildTrustedPath(paths.DotNetRoot);
        result["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        result["DOTNET_NOLOGO"] = "1";
        result["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        result["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        result["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        result["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_INTERVAL_HOURS"] = "999999";
        result["DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK"] = "true";
        result["DOTNET_SDK_VULNERABILITY_CHECK_DISABLE"] = "true";
        result["DOTNET_EnableDiagnostics"] = "0";
        result["NUGET_XMLDOC_MODE"] = "skip";
        result["HOME"] = paths.Home;

        result["XDG_DATA_HOME"] = Path.Combine(
            paths.Home,
            ".local",
            "share");
        result["XDG_CONFIG_HOME"] = Path.Combine(
            paths.Home,
            ".config");
        result["XDG_CACHE_HOME"] = Path.Combine(
            paths.Home,
            ".cache");
        result["DOTNET_CLI_HOME"] = paths.DotNetCliHome;
        result["NUGET_HTTP_CACHE_PATH"] = paths.NuGetHttpCache;
        result["NUGET_PACKAGES"] = paths.GlobalPackages;
        result["TMPDIR"] = paths.Temp;
        result["TEMP"] = paths.Temp;
        result["TMP"] = paths.Temp;

        // No DOTNET_SANDBOX_APPLICATION_GROUP_ID: macOS restricts ~/Library/Group Containers to
        // entitled processes, so redirecting the PAL there fails closed. The child uses the shared
        // /tmp/.dotnet PAL roots instead, granted to the jail explicitly (MacOsDotNetIpcRoots).
        return result;
    }

    internal static void Apply(
        System.Diagnostics.ProcessStartInfo startInfo,
        WorkspaceCheckEnvironmentPaths paths,
        IReadOnlyDictionary<string, string?>? hostEnvironment = null)
    {

        ArgumentNullException.ThrowIfNull(startInfo);

        startInfo.Environment.Clear();

        foreach ((string name, string value) in Build(paths, hostEnvironment))
        {

            startInfo.Environment[name] = value;
        }

    }

    private static string? ReadHostValue(
        IReadOnlyDictionary<string, string?>? hostEnvironment,
        string name)
    {

        if (hostEnvironment is not null
            && hostEnvironment.TryGetValue(name, out string? configured))
        {

            return configured;
        }

        return System.Environment.GetEnvironmentVariable(name);
    }

    private static bool IsSafeValue(string? value)
    {

        if (string.IsNullOrWhiteSpace(value) || value.Length > 4_096)
        {

            return false;
        }

        foreach (char character in value)
        {

            if (character is '\0' or '\r' or '\n')
            {

                return false;
            }

        }

        return true;
    }

    private static string BuildTrustedPath(string dotNetRoot)
    {

        if (OperatingSystem.IsWindows())
        {

            string windows = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.Windows);

            return string.Join(
                Path.PathSeparator,
                Path.Combine(windows, "System32"),
                windows,
                dotNetRoot);
        }

        return string.Join(
            Path.PathSeparator,
            "/usr/bin",
            "/bin",
            "/usr/sbin",
            "/sbin",
            dotNetRoot);
    }
}
