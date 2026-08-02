using System.Diagnostics;

using System.Runtime.InteropServices;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Core.Desktop;

internal enum CompendiumLaunchPlatform
{

    MacOS = 0,

    Windows = 1,

    Linux = 2,

}

/// <summary>
/// Locates Compendium via an installed binary, sibling build output, or <c>dotnet run</c> on the
/// solution project, then starts it with <see cref="ProcessStartInfo.ArgumentList"/> (no shell).
/// </summary>
public sealed class CompendiumLauncher : ICompendiumLauncher
{

    private const string CliFallbackCommand = "arcanum config edit";

    public const string AssemblyName = "RetroDownfall.Compendium.Ux";

    public const string ProjectRelativePath = "src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj";

    private readonly Func<string>? _baseDirectoryOverride;

    private readonly Func<string, bool>? _fileExistsOverride;

    private readonly Func<ProcessStartInfo, bool>? _startOverride;

    private readonly CompendiumLaunchPlatform? _platformOverride;

    private readonly Architecture? _processArchitectureOverride;

    public CompendiumLauncher()
    {
    }

    /// <summary>Test seam for discovery and process start.</summary>
    public CompendiumLauncher(
        Func<string> baseDirectory,
        Func<string, bool> fileExists,
        Func<ProcessStartInfo, bool> startProcess)
    {

        _baseDirectoryOverride = baseDirectory;

        _fileExistsOverride = fileExists;

        _startOverride = startProcess;

    }

    internal CompendiumLauncher(
        Func<string> baseDirectory,
        Func<string, bool> fileExists,
        Func<ProcessStartInfo, bool> startProcess,
        CompendiumLaunchPlatform platform,
        Architecture processArchitecture)
        : this(baseDirectory, fileExists, startProcess)
    {

        _platformOverride = platform;

        _processArchitectureOverride = processArchitecture;

    }

    public string ConfigPath => Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

    public CompendiumLaunchResult TryLaunch()
    {

        string configPath = ConfigPath;

        string deepLinkPayload = ApplicationDeepLinkCodec.Encode(
            CreateSettingsDeepLink());

        List<string> executableCandidates = EnumerateExecutableCandidates().ToList();

        string discoveryLocations = BuildDiscoveryLocations(executableCandidates);

        List<string> existingExecutables = [];

        foreach (string executable in executableCandidates)
        {

            if (!FileExists(executable))
            {

                continue;

            }

            existingExecutables.Add(executable);

            if (TryStart(CreateExecutableStartInfo(executable, deepLinkPayload)))
            {

                return new CompendiumLaunchResult(
                    true,
                    executable,
                    configPath,
                    "Opened Compendium.");

            }

        }

        if (TryFindProject(out string? projectPath) && projectPath is not null)
        {

            ProcessStartInfo startInfo = CreateDevelopmentStartInfo(
                projectPath,
                deepLinkPayload);

            if (TryStart(startInfo))
            {

                return new CompendiumLaunchResult(
                    true,
                    projectPath,
                    configPath,
                    "Started Compendium via dotnet run (development).");

            }

            return new CompendiumLaunchResult(
                false,
                projectPath,
                configPath,
                $"Found the Compendium development project but failed to start it. Discovery locations tried: {discoveryLocations}. Edit {configPath} directly or run: {CreateDevelopmentCommand(deepLinkPayload)}. CLI fallback: {CliFallbackCommand}");

        }

        string developmentCommand = CreateDevelopmentCommand(deepLinkPayload);

        if (existingExecutables.Count > 0)
        {

            return new CompendiumLaunchResult(
                false,
                existingExecutables[0],
                configPath,
                $"Compendium could not be started. Discovery locations tried: {discoveryLocations}. Edit {configPath} directly or run: {developmentCommand}. CLI fallback: {CliFallbackCommand}");

        }

        return new CompendiumLaunchResult(
            false,
            null,
            configPath,
            $"Compendium was not found. Discovery locations tried: {discoveryLocations}. Edit {configPath} directly or run: {developmentCommand}. CLI fallback: {CliFallbackCommand}");

    }

    private static ApplicationDeepLink CreateSettingsDeepLink() =>
        new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.Compendium,
            ApplicationResourceKind.Configuration,
            InitialView: ApplicationInitialView.Settings);

    private static ProcessStartInfo CreateExecutableStartInfo(
        string executable,
        string deepLinkPayload)
    {

        ProcessStartInfo startInfo = new()
        {

            FileName = executable,

            UseShellExecute = false,

            CreateNoWindow = true,

        };

        AddDeepLinkArguments(startInfo, deepLinkPayload);

        return startInfo;

    }

    private static ProcessStartInfo CreateDevelopmentStartInfo(
        string projectPath,
        string deepLinkPayload)
    {

        ProcessStartInfo startInfo = new()
        {

            FileName = "dotnet",

            UseShellExecute = false,

            CreateNoWindow = true,

        };

        startInfo.ArgumentList.Add("run");

        startInfo.ArgumentList.Add("--project");

        startInfo.ArgumentList.Add(projectPath);

        startInfo.ArgumentList.Add("--");

        AddDeepLinkArguments(startInfo, deepLinkPayload);

        return startInfo;

    }

    private static void AddDeepLinkArguments(
        ProcessStartInfo startInfo,
        string deepLinkPayload)
    {

        startInfo.ArgumentList.Add(ApplicationDeepLinkCodec.ArgumentName);

        startInfo.ArgumentList.Add(deepLinkPayload);

    }

    private static string CreateDevelopmentCommand(string deepLinkPayload) =>
        $"dotnet run --project {ProjectRelativePath} -- {ApplicationDeepLinkCodec.ArgumentName} {CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(deepLinkPayload)}";

    private static string BuildDiscoveryLocations(
        IReadOnlyList<string> executableCandidates)
    {

        IEnumerable<string> displayedExecutables = executableCandidates.Select(
            path => $"{ApplicationCandidateKind.Executable}: {path}");

        return string.Join(
            ", ",
            displayedExecutables.Append(
                $"{ApplicationCandidateKind.DevelopmentProject}: {ProjectRelativePath}"));

    }

    private bool TryFindProject(out string? path)
    {

        path = null;

        string? directory = GetBaseDirectory();

        while (!string.IsNullOrEmpty(directory))
        {

            string candidate = Path.Combine(directory, ProjectRelativePath);

            if (FileExists(candidate))
            {

                path = candidate;

                return true;

            }

            string? parent = Directory.GetParent(directory)?.FullName;

            if (string.Equals(parent, directory, StringComparison.Ordinal))
            {

                break;

            }

            directory = parent;

        }

        return false;

    }

    private IEnumerable<string> EnumerateExecutableCandidates()
    {

        string baseDir = GetBaseDirectory();

        CompendiumLaunchPlatform platform = GetPlatform();

        Architecture processArchitecture = GetProcessArchitecture();

        string fileName = platform == CompendiumLaunchPlatform.Windows
            ? $"{AssemblyName}.exe"
            : AssemblyName;

        yield return Path.Combine(baseDir, fileName);

        yield return Path.Combine(baseDir, "..", "RetroDownfall.Compendium.Ux", fileName);

        string? portablePackageDirectory = PortablePackageDirectory(
            platform,
            processArchitecture);

        if (portablePackageDirectory is not null)
        {

            yield return Path.GetFullPath(
                Path.Combine(
                    baseDir,
                    "..",
                    portablePackageDirectory,
                    fileName));

        }

        if (platform == CompendiumLaunchPlatform.MacOS)
        {

            string home = global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(home, "Applications", "Compendium.app", "Contents", "MacOS", AssemblyName);

            yield return Path.Combine("/Applications", "Compendium.app", "Contents", "MacOS", AssemblyName);

        }

        if (platform == CompendiumLaunchPlatform.Linux)
        {

            string home = global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(home, ".local", "bin", AssemblyName);

            yield return Path.Combine("/usr", "local", "bin", AssemblyName);

        }

        if (platform == CompendiumLaunchPlatform.Windows)
        {

            string local = global::System.Environment.GetFolderPath(
                global::System.Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(local, "Compendium", fileName);

        }

    }

    private static string? PortablePackageDirectory(
        CompendiumLaunchPlatform platform,
        Architecture processArchitecture) =>
        platform switch
        {

            CompendiumLaunchPlatform.Windows => "compendium-win-x64",

            CompendiumLaunchPlatform.Linux when processArchitecture == Architecture.X64 =>
                "compendium-linux-x64",

            CompendiumLaunchPlatform.Linux when processArchitecture == Architecture.Arm64 =>
                "compendium-linux-arm64",

            _ => null,

        };

    private CompendiumLaunchPlatform GetPlatform()
    {

        if (_platformOverride is { } platform)
        {

            return platform;

        }

        if (OperatingSystem.IsWindows())
        {

            return CompendiumLaunchPlatform.Windows;

        }

        return OperatingSystem.IsMacOS()
            ? CompendiumLaunchPlatform.MacOS
            : CompendiumLaunchPlatform.Linux;

    }

    private Architecture GetProcessArchitecture() =>
        _processArchitectureOverride ?? RuntimeInformation.ProcessArchitecture;

    private string GetBaseDirectory() =>
        _baseDirectoryOverride?.Invoke() ?? AppContext.BaseDirectory;

    private bool FileExists(string path) =>
        _fileExistsOverride?.Invoke(path) ?? File.Exists(path);

    private bool TryStart(ProcessStartInfo startInfo)
    {

        if (_startOverride is not null)
        {

            return _startOverride(startInfo);

        }

        try
        {

            using Process? process = Process.Start(startInfo);

            return process is not null;

        }
        catch
        {

            return false;

        }

    }

}
