using RetroDownfall.Arcanum.Core.Desktop;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Desktop;

[Collection("ProcessEnvironment")]
public sealed class ApplicationDiscoveryEnvironmentTests
{
    [Fact]

    public void CreateDefault_does_not_adopt_a_repository_root_from_the_current_directory()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-discovery-{Guid.NewGuid():N}");

        string child = Path.Combine(tempDirectory, "child");

        string installedBase = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-installed-{Guid.NewGuid():N}");

        Directory.CreateDirectory(installedBase);

        Directory.CreateDirectory(child);

        File.WriteAllText(
            Path.Combine(tempDirectory, "RetroDownfall.Arcanum.slnx"),
            "<Solution />");

        string originalDirectory = global::System.Environment.CurrentDirectory;

        try
        {
            global::System.Environment.CurrentDirectory = child;

            ApplicationDiscoveryEnvironment environment =
                ApplicationDiscoveryEnvironment.CreateDefault(installedBase);

            Assert.NotEqual(
                Path.GetFullPath(tempDirectory),
                Path.GetFullPath(environment.RepositoryRoot));

            Assert.Equal(
                Path.GetFullPath(installedBase),
                Path.GetFullPath(environment.RepositoryRoot));
        }
        finally
        {
            global::System.Environment.CurrentDirectory = originalDirectory;

            Directory.Delete(tempDirectory, recursive: true);

            Directory.Delete(installedBase, recursive: true);
        }
    }

    /// <summary>
    /// The opt-in is the only gate. A JIT runtime is not a development build: The Forge ships as a
    /// self-contained JIT image, so treating JIT as "development" left the gate permanently open there
    /// and let a planted project above its install directory run through <c>dotnet run</c>.
    /// </summary>
    [Theory]

    [InlineData(null, false)]

    [InlineData("", false)]

    [InlineData("0", false)]

    [InlineData("true", false)]

    [InlineData(" 1", false)]

    [InlineData("1", true)]

    public void The_development_project_is_offered_only_to_a_process_opted_in_with_exactly_1_whatever_its_runtime(
        string? optIn,
        bool expected)
    {
        // Answers only for the variable's literal name, so a drifted constant reads as "unset".
        string? Read(string name) =>
            name == "ARCANUM_DEV_LAUNCHER" ? optIn : null;

        Assert.Equal(
            expected,
            ApplicationDiscoveryEnvironment.DevelopmentProjectLaunchAllowed(Read));
    }

    [Fact]

    public void The_opt_in_variable_name_is_pinned_because_operators_set_it_in_their_environment()
    {
        Assert.Equal("ARCANUM_DEV_LAUNCHER", ApplicationDiscoveryEnvironment.DevelopmentProjectOptInVariable);
    }

    [Fact]

    public void A_native_aot_image_does_not_run_the_development_project_without_an_explicit_opt_in()
    {
        ApplicationDiscoveryEnvironment environment = new(
            BaseDirectory: "/opt/arcanum",
            HomeDirectory: "/home/tester",
            LocalApplicationDataDirectory: "/home/tester/.local/share",
            RepositoryRoot: "/work/arcanum",
            PathExists: _ => true)
        {
            AllowDevelopmentProject = false,
        };

        IReadOnlyList<ApplicationDiscoveryCandidate> candidates =
            new MacOsApplicationDiscoveryService(environment)
                .Discover(DesktopApplication.TheForge);

        ApplicationDiscoveryCandidate project = Assert.Single(
            candidates,
            candidate => candidate.Kind == ApplicationCandidateKind.DevelopmentProject);

        Assert.False(project.Exists);

        Assert.NotNull(project.ProjectRelativePath);
    }
}
