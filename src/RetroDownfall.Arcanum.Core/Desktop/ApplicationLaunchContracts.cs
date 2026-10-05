using System.Diagnostics;

using System.Runtime.CompilerServices;

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

    public bool AllowDevelopmentProject { get; init; } = true;

    public static ApplicationDiscoveryEnvironment CreateDefault() =>
        CreateDefault(AppContext.BaseDirectory);

    public static ApplicationDiscoveryEnvironment CreateDefault(string baseDirectory)
    {
        string homeDirectory = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.UserProfile);

        string localApplicationDataDirectory = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.LocalApplicationData);

        // Anchored on the executable's own directory only. Walking up from the current
        // directory would let a cloned repository carrying the marker file pick the project
        // that `dotnet run --project` executes (R-009).
        string repositoryRoot = FindRepositoryRoot(baseDirectory) ?? baseDirectory;

        return new ApplicationDiscoveryEnvironment(
            baseDirectory,
            homeDirectory,
            localApplicationDataDirectory,
            repositoryRoot,
            static path => File.Exists(path) || Directory.Exists(path))
        {
            AllowDevelopmentProject = DevelopmentProjectLaunchAllowed(),
        };
    }

    /// <summary>
    /// Environment variable that opts a published image into running the repository project, for
    /// both <see cref="ApplicationLauncher"/> and the legacy <see cref="CompendiumLauncher"/> behind
    /// <c>arcanum config open</c> and The Forge. The same variable also makes <c>arcanum serve</c>
    /// auto-start re-launch the host through <c>dotnet &lt;entry assembly&gt; serve</c>; both
    /// meanings are documented together in <c>docs/Compendium.README.md</c>.
    /// </summary>
    public const string DevelopmentProjectOptInVariable = "ARCANUM_DEV_LAUNCHER";

    internal static bool DevelopmentProjectLaunchAllowed() =>
        DevelopmentProjectLaunchAllowed(
            RuntimeFeature.IsDynamicCodeSupported,
            static name => global::System.Environment.GetEnvironmentVariable(name));

    /// <summary>
    /// A JIT build always offers the repository project; a Native AOT image offers it only when the
    /// opt-in variable is exactly <c>1</c>. Separate from the process reads so both arms are
    /// testable from a JIT test host.
    /// </summary>
    internal static bool DevelopmentProjectLaunchAllowed(
        bool isDynamicCodeSupported,
        Func<string, string?> readEnvironmentVariable) =>
        isDynamicCodeSupported
        || string.Equals(
            readEnvironmentVariable(DevelopmentProjectOptInVariable),
            "1",
            StringComparison.Ordinal);

    private static string? FindRepositoryRoot(string startingDirectory)
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

        return null;
    }
}
