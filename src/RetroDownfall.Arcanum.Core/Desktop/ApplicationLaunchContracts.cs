using System.Diagnostics;

using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Core.Desktop;

/// <summary>Kind of packaged or development launch candidate.</summary>
public enum ApplicationCandidateKind
{

    Executable = 0,

    ApplicationBundle = 1,

    DevelopmentProject = 2,

}

/// <summary>A discovered location and its credential-free display representation.</summary>
public sealed record ApplicationDiscoveryCandidate(
    ApplicationCandidateKind Kind,
    string LaunchPath,
    string DisplayPath,
    bool Exists,
    string? ProjectRelativePath = null);

/// <summary>Resolved application launch request.</summary>
public sealed record ApplicationLaunchRequest(
    DesktopApplication Application,
    ApplicationDeepLink? DeepLink,
    string CliFallbackCommand);

/// <summary>Observable result of a launch attempt.</summary>
public enum ApplicationLaunchStatus
{

    ReusedExisting = 0,

    Started = 1,

    Unavailable = 2,

    Failed = 3,

}

/// <summary>Launch outcome with safe discovery and fallback information.</summary>
public sealed record ApplicationLaunchResult(
    ApplicationLaunchStatus Status,
    IReadOnlyList<ApplicationDiscoveryCandidate> TriedCandidates,
    ApplicationDiscoveryCandidate? SelectedCandidate,
    string Message,
    string? DevelopmentFallbackCommand,
    string CliFallbackCommand)
{

    public bool Launched =>
        Status is ApplicationLaunchStatus.ReusedExisting or ApplicationLaunchStatus.Started;

}

/// <summary>Discovers application launch candidates for the active platform.</summary>
public interface IApplicationDiscoveryService
{

    IReadOnlyList<ApplicationDiscoveryCandidate> Discover(DesktopApplication application);

}

/// <summary>Starts a process without exposing shell construction to launch callers.</summary>
public interface IApplicationProcessStarter
{

    bool TryStart(ProcessStartInfo startInfo);

}

/// <summary>Discovers and launches one of Arcanum's desktop applications.</summary>
public interface IApplicationLauncher
{

    ApplicationLaunchResult TryLaunch(ApplicationLaunchRequest request);

}

/// <summary>Deterministic filesystem environment used by platform discovery services.</summary>
public sealed record ApplicationDiscoveryEnvironment(
    string BaseDirectory,
    string HomeDirectory,
    string LocalApplicationDataDirectory,
    string RepositoryRoot,
    Func<string, bool> PathExists)
{

    public Architecture ProcessArchitecture { get; init; } =
        RuntimeInformation.ProcessArchitecture;

    public static ApplicationDiscoveryEnvironment CreateDefault()
    {

        string baseDirectory = AppContext.BaseDirectory;

        string homeDirectory = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.UserProfile);

        string localApplicationDataDirectory = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.LocalApplicationData);

        string repositoryRoot = FindRepositoryRoot(
            baseDirectory,
            global::System.Environment.CurrentDirectory)
            ?? baseDirectory;

        return new ApplicationDiscoveryEnvironment(
            baseDirectory,
            homeDirectory,
            localApplicationDataDirectory,
            repositoryRoot,
            static path => File.Exists(path) || Directory.Exists(path));

    }

    private static string? FindRepositoryRoot(params string[] startingDirectories)
    {

        foreach (string startingDirectory in startingDirectories)
        {

            DirectoryInfo? directory = new(startingDirectory);

            while (directory is not null)
            {

                if (File.Exists(
                    Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
                {

                    return directory.FullName;

                }

                directory = directory.Parent;

            }

        }

        return null;

    }

}
