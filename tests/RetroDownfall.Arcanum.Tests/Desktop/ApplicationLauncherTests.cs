using System.Diagnostics;

using System.Runtime.InteropServices;

using RetroDownfall.Arcanum.Core.Desktop;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Desktop;

public sealed class ApplicationLauncherTests
{
    private const string TheForgeProject =
        "src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj";

    private const string CompendiumProject =
        "src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj";

    public static TheoryData<DesktopApplication, string, string, string> DesktopApplicationCases =>
        new()
        {
            {
                DesktopApplication.TheForge,
                "RetroDownfall.TheForge.Ux",
                "The Forge.app",
                TheForgeProject
            },

            {
                DesktopApplication.Compendium,
                "RetroDownfall.Compendium.Ux",
                "Compendium.app",
                CompendiumProject
            },
        };

    [Fact]

    public void Try_launch_uses_argument_list_without_a_shell()
    {
        ApplicationDiscoveryCandidate candidate = new(
            ApplicationCandidateKind.Executable,
            "/opt/arcanum/RetroDownfall.TheForge.Ux",
            "/opt/arcanum/RetroDownfall.TheForge.Ux",
            Exists: true);

        StubDiscoveryService discovery = new([candidate]);

        RecordingProcessStarter starter = new(true);

        IApplicationLauncher launcher = new ApplicationLauncher(discovery, starter);

        ApplicationDeepLink deepLink = new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Spell,
            "--model=\"\u540d\u5b57\"; $(touch /tmp/not-created) path with spaces",
            "workspace:\u03b1/\u03b2",
            ApplicationInitialView.Workbench,
            "default");

        ApplicationLaunchRequest request = new(
            DesktopApplication.TheForge,
            deepLink,
            "arcanum spell get --model=\"\u540d\u5b57\"; $(touch /tmp/not-created) path with spaces");

        ApplicationLaunchResult result = launcher.TryLaunch(request);

        ProcessStartInfo startInfo = Assert.Single(starter.StartInfos);

        string payload = ApplicationDeepLinkCodec.Encode(deepLink);

        Assert.Equal(ApplicationLaunchStatus.Started, result.Status);

        Assert.True(result.Launched);

        Assert.Equal(candidate, result.SelectedCandidate);

        Assert.Equal([candidate], result.TriedCandidates);

        Assert.Equal(candidate.LaunchPath, startInfo.FileName);

        Assert.False(startInfo.UseShellExecute);

        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));

        Assert.Equal(
            [ApplicationDeepLinkCodec.ArgumentName, payload],
            startInfo.ArgumentList.ToArray());

        Assert.Equal(deepLink, ApplicationDeepLinkCodec.Decode(startInfo.ArgumentList[1]));
    }

    [Fact]

    public void Start_failure_message_names_the_exception_type()
    {
        ApplicationDiscoveryCandidate candidate = new(
            ApplicationCandidateKind.Executable,
            "/opt/arcanum/RetroDownfall.TheForge.Ux",
            "/opt/arcanum/RetroDownfall.TheForge.Ux",
            Exists: true);

        IApplicationLauncher launcher = new ApplicationLauncher(
            new StubDiscoveryService([candidate]),
            new ThrowingProcessStarter(
                new InvalidOperationException("secret path /home/someone/.config/key")));

        ApplicationLaunchResult result = launcher.TryLaunch(
            new ApplicationLaunchRequest(DesktopApplication.TheForge, DeepLink: null, "arcanum center"));

        Assert.Equal(ApplicationLaunchStatus.Failed, result.Status);

        Assert.Contains(nameof(InvalidOperationException), result.Message, StringComparison.Ordinal);

        // The type only: exception message text can carry paths and secrets.
        Assert.DoesNotContain("secret path", result.Message, StringComparison.Ordinal);
    }

    [Fact]

    public void Discovery_failure_message_names_the_exception_type()
    {
        IApplicationLauncher launcher = new ApplicationLauncher(
            new ThrowingDiscoveryService(new UnauthorizedAccessException("denied /private/path")),
            new RecordingProcessStarter());

        ApplicationLaunchResult result = launcher.TryLaunch(
            new ApplicationLaunchRequest(DesktopApplication.TheForge, DeepLink: null, "arcanum center"));

        Assert.Equal(ApplicationLaunchStatus.Failed, result.Status);

        Assert.Contains(nameof(UnauthorizedAccessException), result.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("/private/path", result.Message, StringComparison.Ordinal);
    }

    [Fact]

    public void Try_launch_application_bundle_uses_direct_macos_open_executable()
    {
        const string bundlePath = "/Applications/The Forge.app";

        ApplicationDiscoveryCandidate candidate = new(
            ApplicationCandidateKind.ApplicationBundle,
            bundlePath,
            bundlePath,
            Exists: true);

        StubDiscoveryService discovery = new([candidate]);

        RecordingProcessStarter starter = new(true);

        IApplicationLauncher launcher = new ApplicationLauncher(discovery, starter);

        ApplicationDeepLink deepLink = CreateSessionDeepLink();

        ApplicationLaunchResult result = launcher.TryLaunch(
            new ApplicationLaunchRequest(
                DesktopApplication.TheForge,
                deepLink,
                $"arcanum session show {deepLink.ResourceId}"));

        ProcessStartInfo startInfo = Assert.Single(starter.StartInfos);

        Assert.Equal(ApplicationLaunchStatus.Started, result.Status);

        Assert.Equal("/usr/bin/open", startInfo.FileName);

        Assert.False(startInfo.UseShellExecute);

        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));

        Assert.Equal(
            [
                "-n",
                bundlePath,
                "--args",
                ApplicationDeepLinkCodec.ArgumentName,
                ApplicationDeepLinkCodec.Encode(deepLink),
            ],
            startInfo.ArgumentList.ToArray());
    }

    [Fact]

    public void Try_launch_continues_after_a_candidate_fails_to_start()
    {
        ApplicationDiscoveryCandidate installedCandidate = new(
            ApplicationCandidateKind.Executable,
            "/Applications/Arcanum/RetroDownfall.TheForge.Ux",
            "/Applications/Arcanum/RetroDownfall.TheForge.Ux",
            Exists: true);

        ApplicationDiscoveryCandidate developmentCandidate = new(
            ApplicationCandidateKind.DevelopmentProject,
            $"/work/arcanum/{TheForgeProject}",
            TheForgeProject,
            Exists: true,
            ProjectRelativePath: TheForgeProject);

        StubDiscoveryService discovery = new(
            [installedCandidate, developmentCandidate]);

        RecordingProcessStarter starter = new(false, true);

        IApplicationLauncher launcher = new ApplicationLauncher(discovery, starter);

        ApplicationDeepLink deepLink = CreateSessionDeepLink();

        ApplicationLaunchResult result = launcher.TryLaunch(
            new ApplicationLaunchRequest(
                DesktopApplication.TheForge,
                deepLink,
                $"arcanum session show {deepLink.ResourceId}"));

        Assert.Equal(ApplicationLaunchStatus.Started, result.Status);

        Assert.True(result.Launched);

        Assert.Equal(developmentCandidate, result.SelectedCandidate);

        Assert.Equal(
            [installedCandidate, developmentCandidate],
            result.TriedCandidates);

        Assert.Equal(2, starter.StartInfos.Count);

        ProcessStartInfo developmentStart = starter.StartInfos[1];

        Assert.Equal("dotnet", developmentStart.FileName);

        Assert.False(developmentStart.UseShellExecute);

        Assert.Equal(
            [
                "run",
                "--project",
                developmentCandidate.LaunchPath,
                "--",
                ApplicationDeepLinkCodec.ArgumentName,
                ApplicationDeepLinkCodec.Encode(deepLink),
            ],
            developmentStart.ArgumentList.ToArray());
    }

    [Fact]

    public void Try_launch_unavailable_reports_every_candidate_and_safe_fallbacks()
    {
        ApplicationDiscoveryCandidate installedCandidate = new(
            ApplicationCandidateKind.Executable,
            "/Applications/The Forge.app/Contents/MacOS/RetroDownfall.TheForge.Ux",
            "/Applications/The Forge.app",
            Exists: false);

        ApplicationDiscoveryCandidate developmentCandidate = new(
            ApplicationCandidateKind.DevelopmentProject,
            $"/Users/developer/source/arcanum/{TheForgeProject}",
            TheForgeProject,
            Exists: false,
            ProjectRelativePath: TheForgeProject);

        StubDiscoveryService discovery = new(
            [installedCandidate, developmentCandidate]);

        RecordingProcessStarter starter = new();

        IApplicationLauncher launcher = new ApplicationLauncher(discovery, starter);

        ApplicationDeepLink deepLink = CreateSessionDeepLink();

        string cliFallback = $"arcanum session show {deepLink.ResourceId}";

        ApplicationLaunchResult result = launcher.TryLaunch(
            new ApplicationLaunchRequest(
                DesktopApplication.TheForge,
                deepLink,
                cliFallback));

        Assert.Equal(ApplicationLaunchStatus.Unavailable, result.Status);

        Assert.False(result.Launched);

        Assert.Null(result.SelectedCandidate);

        Assert.Equal(
            [installedCandidate, developmentCandidate],
            result.TriedCandidates);

        Assert.Empty(starter.StartInfos);

        Assert.Equal(cliFallback, result.CliFallbackCommand);

        Assert.NotNull(result.DevelopmentFallbackCommand);

        Assert.StartsWith(
            $"dotnet run --project {TheForgeProject}",
            result.DevelopmentFallbackCommand,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "/Users/developer",
            result.DevelopmentFallbackCommand,
            StringComparison.Ordinal);

        string deepLinkPayload = ApplicationDeepLinkCodec.Encode(deepLink);

        Assert.EndsWith(
            $"{ApplicationDeepLinkCodec.ArgumentName} {CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(deepLinkPayload)}",
            result.DevelopmentFallbackCommand,
            StringComparison.Ordinal);

        Assert.Contains(
            installedCandidate.DisplayPath,
            result.Message,
            StringComparison.Ordinal);

        Assert.Contains(
            developmentCandidate.DisplayPath,
            result.Message,
            StringComparison.Ordinal);
    }

    [Theory]

    [MemberData(nameof(DesktopApplicationCases))]

    public void Mac_discovery_includes_application_bundle_and_relative_development_project(
        DesktopApplication application,
        string assemblyName,
        string applicationBundle,
        string projectRelativePath)
    {
        ApplicationDiscoveryEnvironment environment = CreateDiscoveryEnvironment();

        IApplicationDiscoveryService discovery = new MacOsApplicationDiscoveryService(environment);

        IReadOnlyList<ApplicationDiscoveryCandidate> candidates = discovery.Discover(application);

        Assert.Contains(
            candidates,
            candidate =>
                candidate.Kind == ApplicationCandidateKind.ApplicationBundle
                && candidate.LaunchPath.EndsWith(applicationBundle, StringComparison.Ordinal));

        Assert.Contains(
            candidates,
            candidate =>
                candidate.Kind == ApplicationCandidateKind.Executable
                && candidate.LaunchPath.EndsWith(assemblyName, StringComparison.Ordinal));

        AssertDevelopmentCandidate(candidates, projectRelativePath);

        AssertSafeDisplayPaths(candidates);
    }

    [Theory]

    [MemberData(nameof(DesktopApplicationCases))]

    public void Windows_discovery_includes_executable_and_relative_development_project(
        DesktopApplication application,
        string assemblyName,
        string applicationBundle,
        string projectRelativePath)
    {
        _ = applicationBundle;

        ApplicationDiscoveryEnvironment environment = CreateDiscoveryEnvironment();

        IApplicationDiscoveryService discovery = new WindowsApplicationDiscoveryService(environment);

        IReadOnlyList<ApplicationDiscoveryCandidate> candidates = discovery.Discover(application);

        Assert.Contains(
            candidates,
            candidate =>
                candidate.Kind == ApplicationCandidateKind.Executable
                && NormalizePath(candidate.LaunchPath)
                    .EndsWith($"/{assemblyName}.exe", StringComparison.Ordinal));

        AssertDevelopmentCandidate(candidates, projectRelativePath);

        AssertSafeDisplayPaths(candidates);
    }

    [Theory]

    [InlineData(
        DesktopApplication.TheForge,
        "the-forge-win-x64",
        "RetroDownfall.TheForge.Ux.exe")]

    [InlineData(
        DesktopApplication.Compendium,
        "compendium-win-x64",
        "RetroDownfall.Compendium.Ux.exe")]

    public void Windows_discovery_includes_side_by_side_portable_package(
        DesktopApplication application,
        string packageDirectory,
        string executableName)
    {
        ApplicationDiscoveryEnvironment environment = CreateDiscoveryEnvironment();

        IApplicationDiscoveryService discovery = new WindowsApplicationDiscoveryService(environment);

        IReadOnlyList<ApplicationDiscoveryCandidate> candidates = discovery.Discover(application);

        string expectedPath = Path.GetFullPath(
            Path.Combine(
                environment.BaseDirectory,
                "..",
                packageDirectory,
                executableName));

        Assert.Contains(
            candidates,
            candidate =>
                candidate.Kind == ApplicationCandidateKind.Executable
                && string.Equals(
                    candidate.LaunchPath,
                    expectedPath,
                    StringComparison.Ordinal));
    }

    [Theory]

    [MemberData(nameof(DesktopApplicationCases))]

    public void Linux_discovery_includes_local_binary_and_relative_development_project(
        DesktopApplication application,
        string assemblyName,
        string applicationBundle,
        string projectRelativePath)
    {
        _ = applicationBundle;

        ApplicationDiscoveryEnvironment environment = CreateDiscoveryEnvironment();

        IApplicationDiscoveryService discovery = new LinuxApplicationDiscoveryService(environment);

        IReadOnlyList<ApplicationDiscoveryCandidate> candidates = discovery.Discover(application);

        Assert.Contains(
            candidates,
            candidate =>
                candidate.Kind == ApplicationCandidateKind.Executable
                && NormalizePath(candidate.LaunchPath)
                    .EndsWith($"/.local/bin/{assemblyName}", StringComparison.Ordinal));

        AssertDevelopmentCandidate(candidates, projectRelativePath);

        AssertSafeDisplayPaths(candidates);
    }

    [Theory]

    [InlineData(
        DesktopApplication.TheForge,
        Architecture.X64,
        "the-forge-linux-x64",
        "the-forge-linux-arm64",
        "RetroDownfall.TheForge.Ux")]

    [InlineData(
        DesktopApplication.TheForge,
        Architecture.Arm64,
        "the-forge-linux-arm64",
        "the-forge-linux-x64",
        "RetroDownfall.TheForge.Ux")]

    [InlineData(
        DesktopApplication.Compendium,
        Architecture.X64,
        "compendium-linux-x64",
        "compendium-linux-arm64",
        "RetroDownfall.Compendium.Ux")]

    [InlineData(
        DesktopApplication.Compendium,
        Architecture.Arm64,
        "compendium-linux-arm64",
        "compendium-linux-x64",
        "RetroDownfall.Compendium.Ux")]

    public void Linux_discovery_includes_only_the_active_architecture_portable_package(
        DesktopApplication application,
        Architecture processArchitecture,
        string activePackageDirectory,
        string otherPackageDirectory,
        string executableName)
    {
        ApplicationDiscoveryEnvironment environment = CreateDiscoveryEnvironment(
            processArchitecture);

        IApplicationDiscoveryService discovery = new LinuxApplicationDiscoveryService(environment);

        IReadOnlyList<ApplicationDiscoveryCandidate> candidates = discovery.Discover(application);

        string expectedPath = Path.GetFullPath(
            Path.Combine(
                environment.BaseDirectory,
                "..",
                activePackageDirectory,
                executableName));

        Assert.Contains(
            candidates,
            candidate =>
                candidate.Kind == ApplicationCandidateKind.Executable
                && string.Equals(
                    candidate.LaunchPath,
                    expectedPath,
                    StringComparison.Ordinal));

        Assert.DoesNotContain(
            candidates,
            candidate => candidate.LaunchPath.Contains(
                otherPackageDirectory,
                StringComparison.Ordinal));
    }

    [Theory]

    [InlineData(
        (int)CompendiumLaunchPlatform.Windows,
        Architecture.X64,
        "compendium-win-x64",
        "RetroDownfall.Compendium.Ux.exe")]

    [InlineData(
        (int)CompendiumLaunchPlatform.Linux,
        Architecture.X64,
        "compendium-linux-x64",
        "RetroDownfall.Compendium.Ux")]

    [InlineData(
        (int)CompendiumLaunchPlatform.Linux,
        Architecture.Arm64,
        "compendium-linux-arm64",
        "RetroDownfall.Compendium.Ux")]

    public void Legacy_compendium_launcher_discovers_side_by_side_portable_package(
        int platformValue,
        Architecture processArchitecture,
        string packageDirectory,
        string executableName)
    {
        CompendiumLaunchPlatform platform = (CompendiumLaunchPlatform)platformValue;

        string baseDirectory = Path.Combine(
            Path.GetTempPath(),
            "packages",
            platform == CompendiumLaunchPlatform.Windows
                ? "arcanum-win-x64"
                : $"arcanum-linux-{ArchitectureSuffix(processArchitecture)}");

        string packagedExecutable = Path.GetFullPath(
            Path.Combine(
                baseDirectory,
                "..",
                packageDirectory,
                executableName));

        ProcessStartInfo? started = null;

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, packagedExecutable, StringComparison.Ordinal),
            startInfo =>
            {
                started = startInfo;

                return true;
            },
            platform,
            processArchitecture);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.True(result.Launched);

        Assert.Equal(packagedExecutable, result.ExecutablePath);

        Assert.NotNull(started);

        Assert.Equal(packagedExecutable, started!.FileName);
    }

    [Fact]

    public void Legacy_compendium_launcher_names_the_exception_type_when_an_executable_fails_to_start()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "compendium-start-failure");

        string executable = Path.Combine(baseDirectory, "RetroDownfall.Compendium.Ux");

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, executable, StringComparison.Ordinal),
            _ => throw new InvalidOperationException("secret path /home/someone/.config/key"),
            CompendiumLaunchPlatform.MacOS,
            Architecture.X64);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.False(result.Launched);

        Assert.Equal(executable, result.ExecutablePath);

        Assert.Contains(nameof(InvalidOperationException), result.Message, StringComparison.Ordinal);

        // The type only: exception message text can carry paths and secrets.
        Assert.DoesNotContain("secret path", result.Message, StringComparison.Ordinal);
    }

    [Fact]

    public void Legacy_compendium_launcher_names_the_exception_type_when_the_development_project_fails_to_start()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "compendium-dev-start-failure");

        string baseDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");

        string projectPath = Path.Combine(repositoryRoot, CompendiumLauncher.ProjectRelativePath);

        string marker = Path.Combine(repositoryRoot, "RetroDownfall.Arcanum.slnx");

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, projectPath, StringComparison.Ordinal)
                || string.Equals(path, marker, StringComparison.Ordinal),
            _ => throw new UnauthorizedAccessException("denied /private/path"),
            CompendiumLaunchPlatform.MacOS,
            Architecture.X64,
            allowDevelopmentProject: true);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.False(result.Launched);

        Assert.Equal(projectPath, result.ExecutablePath);

        Assert.Contains(nameof(UnauthorizedAccessException), result.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("/private/path", result.Message, StringComparison.Ordinal);
    }

    [Fact]

    public void Legacy_compendium_launcher_does_not_blame_the_development_project_for_an_earlier_executable_exception()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "compendium-mixed-start-failure");

        string baseDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");

        string executable = Path.Combine(baseDirectory, "RetroDownfall.Compendium.Ux");

        string projectPath = Path.Combine(repositoryRoot, CompendiumLauncher.ProjectRelativePath);

        string marker = Path.Combine(repositoryRoot, "RetroDownfall.Arcanum.slnx");

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, executable, StringComparison.Ordinal)
                || string.Equals(path, projectPath, StringComparison.Ordinal)
                || string.Equals(path, marker, StringComparison.Ordinal),
            startInfo => startInfo.FileName == executable
                ? throw new InvalidOperationException("executable refused")
                : false,
            CompendiumLaunchPlatform.MacOS,
            Architecture.X64,
            allowDevelopmentProject: true);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.False(result.Launched);

        Assert.Equal(projectPath, result.ExecutablePath);

        Assert.Contains("failed to start it.", result.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(nameof(InvalidOperationException), result.Message, StringComparison.Ordinal);
    }

    [Fact]

    public void Legacy_compendium_launcher_never_runs_the_development_project_without_the_opt_in()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "compendium-dev-not-opted-in");

        string baseDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");

        string projectPath = Path.Combine(repositoryRoot, CompendiumLauncher.ProjectRelativePath);

        List<ProcessStartInfo> started = [];

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, projectPath, StringComparison.Ordinal),
            startInfo =>
            {
                started.Add(startInfo);

                return true;
            },
            CompendiumLaunchPlatform.MacOS,
            Architecture.X64,
            allowDevelopmentProject: false);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.Empty(started);

        Assert.False(result.Launched);

        Assert.Null(result.ExecutablePath);

        // The display-only fallback is still printed, repository-relative.
        Assert.Contains(
            $"dotnet run --project {CompendiumLauncher.ProjectRelativePath}",
            result.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain(projectPath, result.Message, StringComparison.Ordinal);
    }

    [Fact]

    public void Legacy_compendium_launcher_still_prefers_an_installed_executable_without_the_opt_in()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "compendium-dev-not-opted-in-installed");

        string baseDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");

        string executable = Path.Combine(baseDirectory, "RetroDownfall.Compendium.Ux");

        string projectPath = Path.Combine(repositoryRoot, CompendiumLauncher.ProjectRelativePath);

        List<ProcessStartInfo> started = [];

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, executable, StringComparison.Ordinal)
                || string.Equals(path, projectPath, StringComparison.Ordinal),
            startInfo =>
            {
                started.Add(startInfo);

                return startInfo.FileName == executable;
            },
            CompendiumLaunchPlatform.MacOS,
            Architecture.X64,
            allowDevelopmentProject: false);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.True(result.Launched);

        Assert.Equal(executable, result.ExecutablePath);

        Assert.Single(started);
    }

    [Fact]

    public void Legacy_compendium_launcher_runs_the_development_project_when_it_is_allowed()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), "compendium-dev-opted-in");

        string baseDirectory = Path.Combine(repositoryRoot, "artifacts", "bin");

        string projectPath = Path.Combine(repositoryRoot, CompendiumLauncher.ProjectRelativePath);

        ProcessStartInfo? started = null;

        string marker = Path.Combine(repositoryRoot, "RetroDownfall.Arcanum.slnx");

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, projectPath, StringComparison.Ordinal)
                || string.Equals(path, marker, StringComparison.Ordinal),
            startInfo =>
            {
                started = startInfo;

                return true;
            },
            CompendiumLaunchPlatform.MacOS,
            Architecture.X64,
            allowDevelopmentProject: true);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.True(result.Launched);

        Assert.Equal(projectPath, result.ExecutablePath);

        Assert.NotNull(started);

        Assert.Equal("dotnet", started!.FileName);
    }

    /// <summary>
    /// The project walk accepted the first ancestor holding the Compendium project path, with no
    /// repository marker, so any writable ancestor of the install directory (for example a folder
    /// another account created at a drive root) could supply the project that <c>dotnet run</c>
    /// executes. It now accepts only an ancestor that also holds the solution marker, as the
    /// <c>arcanum open</c> discovery does.
    /// </summary>
    [Fact]

    public void Legacy_compendium_launcher_ignores_a_project_path_without_the_repository_marker_beside_it()
    {
        string plantedRoot = Path.Combine(Path.GetTempPath(), "compendium-planted-root");

        string baseDirectory = Path.Combine(plantedRoot, "Downloads", "the-forge");

        string projectPath = Path.Combine(plantedRoot, CompendiumLauncher.ProjectRelativePath);

        List<ProcessStartInfo> started = [];

        CompendiumLauncher launcher = new(
            () => baseDirectory,
            path => string.Equals(path, projectPath, StringComparison.Ordinal),
            startInfo =>
            {
                started.Add(startInfo);

                return true;
            },
            CompendiumLaunchPlatform.MacOS,
            Architecture.X64,
            allowDevelopmentProject: true);

        CompendiumLaunchResult result = launcher.TryLaunch();

        Assert.Empty(started);

        Assert.False(result.Launched);

        Assert.Null(result.ExecutablePath);
    }

    private static ApplicationDeepLink CreateSessionDeepLink() =>
        new(
            ApplicationDeepLink.CurrentSchemaVersion,
            DesktopApplication.TheForge,
            ApplicationResourceKind.Session,
            Guid.NewGuid().ToString("D"),
            InitialView: ApplicationInitialView.Workbench,
            ConnectionProfileId: "default");

    private static ApplicationDiscoveryEnvironment CreateDiscoveryEnvironment(
        Architecture processArchitecture = Architecture.X64) =>
        new(
            BaseDirectory: "/opt/arcanum",
            HomeDirectory: "/home/tester",
            LocalApplicationDataDirectory: "C:/Users/tester/AppData/Local",
            RepositoryRoot: "/work/arcanum",
            PathExists: _ => true)
        {
            ProcessArchitecture = processArchitecture,
        };

    private static string ArchitectureSuffix(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => "x64",

            Architecture.Arm64 => "arm64",

            _ => throw new ArgumentOutOfRangeException(
                nameof(architecture),
                architecture,
                "The test architecture is unsupported."),
        };

    private static void AssertDevelopmentCandidate(
        IReadOnlyList<ApplicationDiscoveryCandidate> candidates,
        string projectRelativePath)
    {
        ApplicationDiscoveryCandidate candidate = Assert.Single(
            candidates,
            candidate =>
                candidate.Kind == ApplicationCandidateKind.DevelopmentProject
                && string.Equals(
                    candidate.ProjectRelativePath,
                    projectRelativePath,
                    StringComparison.Ordinal));

        Assert.Equal(projectRelativePath, candidate.DisplayPath);

        Assert.DoesNotContain(
            "/work/arcanum",
            candidate.DisplayPath,
            StringComparison.Ordinal);
    }

    private static void AssertSafeDisplayPaths(
        IReadOnlyList<ApplicationDiscoveryCandidate> candidates)
    {
        Assert.NotEmpty(candidates);

        Assert.All(
            candidates,
            candidate =>
            {
                Assert.False(string.IsNullOrWhiteSpace(candidate.DisplayPath));

                Assert.DoesNotContain(
                    "apiKey",
                    candidate.DisplayPath,
                    StringComparison.OrdinalIgnoreCase);

                Assert.DoesNotContain(
                    "credential",
                    candidate.DisplayPath,
                    StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private sealed class StubDiscoveryService : IApplicationDiscoveryService
    {
        private readonly IReadOnlyList<ApplicationDiscoveryCandidate> _candidates;

        public StubDiscoveryService(IReadOnlyList<ApplicationDiscoveryCandidate> candidates)
        {
            _candidates = candidates;
        }

        public IReadOnlyList<ApplicationDiscoveryCandidate> Discover(
            DesktopApplication application) =>
            _candidates;
    }

    private sealed class ThrowingDiscoveryService(Exception exception) : IApplicationDiscoveryService
    {
        public IReadOnlyList<ApplicationDiscoveryCandidate> Discover(
            DesktopApplication application) =>
            throw exception;
    }

    private sealed class ThrowingProcessStarter(Exception exception) : IApplicationProcessStarter
    {
        public bool TryStart(ProcessStartInfo startInfo) => throw exception;
    }

    private sealed class RecordingProcessStarter : IApplicationProcessStarter
    {
        private readonly Queue<bool> _outcomes;

        public RecordingProcessStarter(params bool[] outcomes)
        {
            _outcomes = new Queue<bool>(outcomes);
        }

        public List<ProcessStartInfo> StartInfos { get; } = [];

        public bool TryStart(ProcessStartInfo startInfo)
        {
            StartInfos.Add(startInfo);

            return _outcomes.Count > 0 && _outcomes.Dequeue();
        }
    }
}
