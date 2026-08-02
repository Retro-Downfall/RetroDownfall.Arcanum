using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Core.Desktop;

/// <summary>Creates the platform discovery implementation used by the current process.</summary>
public static class ApplicationDiscoveryServiceFactory
{

    public static IApplicationDiscoveryService CreateDefault()
    {

        ApplicationDiscoveryEnvironment environment =
            ApplicationDiscoveryEnvironment.CreateDefault();

        if (OperatingSystem.IsMacOS())
        {

            return new MacOsApplicationDiscoveryService(environment);

        }

        if (OperatingSystem.IsWindows())
        {

            return new WindowsApplicationDiscoveryService(environment);

        }

        return new LinuxApplicationDiscoveryService(environment);

    }

}

/// <summary>Discovers executable, app-bundle, and project candidates on macOS.</summary>
public sealed class MacOsApplicationDiscoveryService : IApplicationDiscoveryService
{

    private readonly ApplicationDiscoveryEnvironment _environment;

    public MacOsApplicationDiscoveryService(ApplicationDiscoveryEnvironment environment)
    {

        _environment = environment;

    }

    public IReadOnlyList<ApplicationDiscoveryCandidate> Discover(
        DesktopApplication application)
    {

        ApplicationDescriptor descriptor = ApplicationDescriptor.For(application);

        List<ApplicationDiscoveryCandidate> candidates = [];

        ApplicationDiscoveryCandidateBuilder.AddExecutableCandidates(
            candidates,
            _environment,
            descriptor,
            executableSuffix: string.Empty);

        ApplicationDiscoveryCandidateBuilder.Add(
            candidates,
            _environment,
            ApplicationCandidateKind.ApplicationBundle,
            Path.Combine(
                _environment.HomeDirectory,
                "Applications",
                descriptor.ApplicationBundleName),
            Path.Combine(
                _environment.HomeDirectory,
                "Applications",
                descriptor.ApplicationBundleName));

        ApplicationDiscoveryCandidateBuilder.Add(
            candidates,
            _environment,
            ApplicationCandidateKind.ApplicationBundle,
            Path.Combine("/Applications", descriptor.ApplicationBundleName),
            Path.Combine("/Applications", descriptor.ApplicationBundleName));

        ApplicationDiscoveryCandidateBuilder.AddDevelopmentCandidate(
            candidates,
            _environment,
            descriptor);

        return candidates;

    }

}

/// <summary>Discovers executable and project candidates on Windows.</summary>
public sealed class WindowsApplicationDiscoveryService : IApplicationDiscoveryService
{

    private readonly ApplicationDiscoveryEnvironment _environment;

    public WindowsApplicationDiscoveryService(ApplicationDiscoveryEnvironment environment)
    {

        _environment = environment;

    }

    public IReadOnlyList<ApplicationDiscoveryCandidate> Discover(
        DesktopApplication application)
    {

        ApplicationDescriptor descriptor = ApplicationDescriptor.For(application);

        List<ApplicationDiscoveryCandidate> candidates = [];

        ApplicationDiscoveryCandidateBuilder.AddExecutableCandidates(
            candidates,
            _environment,
            descriptor,
            executableSuffix: ".exe");

        ApplicationDiscoveryCandidateBuilder.AddPortableSiblingCandidate(
            candidates,
            _environment,
            descriptor,
            platformSuffix: "win-x64",
            executableSuffix: ".exe");

        string installedExecutable = Path.Combine(
            _environment.LocalApplicationDataDirectory,
            descriptor.ProductDirectoryName,
            $"{descriptor.AssemblyName}.exe");

        ApplicationDiscoveryCandidateBuilder.Add(
            candidates,
            _environment,
            ApplicationCandidateKind.Executable,
            installedExecutable,
            installedExecutable);

        ApplicationDiscoveryCandidateBuilder.AddDevelopmentCandidate(
            candidates,
            _environment,
            descriptor);

        return candidates;

    }

}

/// <summary>Discovers executable and project candidates on Linux.</summary>
public sealed class LinuxApplicationDiscoveryService : IApplicationDiscoveryService
{

    private readonly ApplicationDiscoveryEnvironment _environment;

    public LinuxApplicationDiscoveryService(ApplicationDiscoveryEnvironment environment)
    {

        _environment = environment;

    }

    public IReadOnlyList<ApplicationDiscoveryCandidate> Discover(
        DesktopApplication application)
    {

        ApplicationDescriptor descriptor = ApplicationDescriptor.For(application);

        List<ApplicationDiscoveryCandidate> candidates = [];

        ApplicationDiscoveryCandidateBuilder.AddExecutableCandidates(
            candidates,
            _environment,
            descriptor,
            executableSuffix: string.Empty);

        string? architectureSuffix = LinuxArchitectureSuffix(
            _environment.ProcessArchitecture);

        if (architectureSuffix is not null)
        {

            ApplicationDiscoveryCandidateBuilder.AddPortableSiblingCandidate(
                candidates,
                _environment,
                descriptor,
                platformSuffix: $"linux-{architectureSuffix}",
                executableSuffix: string.Empty);

        }

        string userExecutable = Path.Combine(
            _environment.HomeDirectory,
            ".local",
            "bin",
            descriptor.AssemblyName);

        ApplicationDiscoveryCandidateBuilder.Add(
            candidates,
            _environment,
            ApplicationCandidateKind.Executable,
            userExecutable,
            userExecutable);

        string systemExecutable = Path.Combine(
            "/usr",
            "local",
            "bin",
            descriptor.AssemblyName);

        ApplicationDiscoveryCandidateBuilder.Add(
            candidates,
            _environment,
            ApplicationCandidateKind.Executable,
            systemExecutable,
            systemExecutable);

        ApplicationDiscoveryCandidateBuilder.AddDevelopmentCandidate(
            candidates,
            _environment,
            descriptor);

        return candidates;

    }

    private static string? LinuxArchitectureSuffix(Architecture architecture) =>
        architecture switch
        {

            Architecture.X64 => "x64",

            Architecture.Arm64 => "arm64",

            _ => null,

        };

}

internal sealed record ApplicationDescriptor(
    string AssemblyName,
    string ApplicationBundleName,
    string ProductDirectoryName,
    string PortableProductName,
    string ProjectRelativePath)
{

    public static ApplicationDescriptor For(DesktopApplication application) =>
        application switch
        {

            DesktopApplication.CommandCenter => new ApplicationDescriptor(
                "arcanum",
                "Arcanum.app",
                "Arcanum",
                "arcanum",
                "src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"),

            DesktopApplication.TheForge => new ApplicationDescriptor(
                "RetroDownfall.TheForge.Ux",
                "The Forge.app",
                "The Forge",
                "the-forge",
                "src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj"),

            DesktopApplication.Compendium => new ApplicationDescriptor(
                "RetroDownfall.Compendium.Ux",
                "Compendium.app",
                "Compendium",
                "compendium",
                "src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj"),

            _ => throw new ArgumentOutOfRangeException(
                nameof(application),
                application,
                "Unknown desktop application."),

        };

}

internal static class ApplicationDiscoveryCandidateBuilder
{

    public static void AddExecutableCandidates(
        List<ApplicationDiscoveryCandidate> candidates,
        ApplicationDiscoveryEnvironment environment,
        ApplicationDescriptor descriptor,
        string executableSuffix)
    {

        string executableName = $"{descriptor.AssemblyName}{executableSuffix}";

        string baseExecutable = Path.Combine(
            environment.BaseDirectory,
            executableName);

        Add(
            candidates,
            environment,
            ApplicationCandidateKind.Executable,
            baseExecutable,
            baseExecutable);

        string siblingExecutable = Path.GetFullPath(
            Path.Combine(
                environment.BaseDirectory,
                "..",
                descriptor.AssemblyName,
                executableName));

        Add(
            candidates,
            environment,
            ApplicationCandidateKind.Executable,
            siblingExecutable,
            siblingExecutable);

    }

    public static void AddDevelopmentCandidate(
        List<ApplicationDiscoveryCandidate> candidates,
        ApplicationDiscoveryEnvironment environment,
        ApplicationDescriptor descriptor)
    {

        string projectPath = Path.Combine(
            environment.RepositoryRoot,
            descriptor.ProjectRelativePath);

        Add(
            candidates,
            environment,
            ApplicationCandidateKind.DevelopmentProject,
            projectPath,
            descriptor.ProjectRelativePath,
            descriptor.ProjectRelativePath);

    }

    public static void AddPortableSiblingCandidate(
        List<ApplicationDiscoveryCandidate> candidates,
        ApplicationDiscoveryEnvironment environment,
        ApplicationDescriptor descriptor,
        string platformSuffix,
        string executableSuffix)
    {

        string executableName = $"{descriptor.AssemblyName}{executableSuffix}";

        string portableExecutable = Path.GetFullPath(
            Path.Combine(
                environment.BaseDirectory,
                "..",
                $"{descriptor.PortableProductName}-{platformSuffix}",
                executableName));

        Add(
            candidates,
            environment,
            ApplicationCandidateKind.Executable,
            portableExecutable,
            portableExecutable);

    }

    public static void Add(
        List<ApplicationDiscoveryCandidate> candidates,
        ApplicationDiscoveryEnvironment environment,
        ApplicationCandidateKind kind,
        string launchPath,
        string displayPath,
        string? projectRelativePath = null)
    {

        bool exists;

        try
        {

            exists = environment.PathExists(launchPath);

        }
        catch
        {

            exists = false;

        }

        candidates.Add(
            new ApplicationDiscoveryCandidate(
                kind,
                launchPath,
                displayPath,
                exists,
                projectRelativePath));

    }

}
