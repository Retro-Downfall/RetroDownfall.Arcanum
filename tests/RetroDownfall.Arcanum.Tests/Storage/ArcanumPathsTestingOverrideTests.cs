using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

[Collection("ProcessEnvironment")]
public sealed class ArcanumPathsTestingOverrideTests
{

    private const string TestHomeVariable = "ARCANUM_TEST_HOME";

    [Fact]
    public void Persistent_paths_use_explicit_home_in_testing_environment()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        WithEnvironment(
            dotnetEnvironment: "Testing",
            aspNetCoreEnvironment: "Testing",
            testHome,
            () =>
            {

                string expectedRoot = Path.Combine(Path.GetFullPath(testHome), ".config", "arcanum");

                Assert.Equal(expectedRoot, ArcanumPaths.GrimoireDirectory);

                Assert.Equal(expectedRoot, ArcanumPaths.SecretStoreDirectory);

                Assert.Equal(Path.Combine(expectedRoot, "arcanum.db"), ArcanumPaths.GrimoireDatabaseFile);

                Assert.Equal(Path.Combine(expectedRoot, "mcp.json"), ArcanumPaths.GlobalMcpConfigFile);

                Assert.Equal(Path.Combine(expectedRoot, "logs"), ArcanumPaths.LogDirectory);

            });

    }

    [Fact]
    public void Persistent_paths_ignore_test_home_outside_testing_environment()
    {

        string testHome = Path.Combine(Path.GetTempPath(), "arcanum-tests", Guid.NewGuid().ToString("N"));

        WithEnvironment(
            dotnetEnvironment: "Production",
            aspNetCoreEnvironment: "Production",
            testHome,
            () =>
            {

                string expectedGrimoire = Path.Combine(
                    global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.UserProfile),
                    ".config",
                    "arcanum");

                string expectedSecrets = Path.Combine(
                    global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.ApplicationData),
                    "arcanum");

                Assert.Equal(expectedGrimoire, ArcanumPaths.GrimoireDirectory);

                Assert.Equal(expectedSecrets, ArcanumPaths.SecretStoreDirectory);

                Assert.Equal(Path.Combine(expectedGrimoire, "mcp.json"), ArcanumPaths.GlobalMcpConfigFile);

                Assert.Equal(Path.Combine(expectedSecrets, "logs"), ArcanumPaths.LogDirectory);

            });

    }

    private static void WithEnvironment(
        string dotnetEnvironment,
        string aspNetCoreEnvironment,
        string testHome,
        Action assertion)
    {

        string? originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        string? originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        string? originalTestHome = global::System.Environment.GetEnvironmentVariable(TestHomeVariable);

        try
        {

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnetEnvironment);

            global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspNetCoreEnvironment);

            global::System.Environment.SetEnvironmentVariable(TestHomeVariable, testHome);

            assertion();

        }
        finally
        {

            global::System.Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", originalDotnetEnvironment);

            global::System.Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", originalAspNetCoreEnvironment);

            global::System.Environment.SetEnvironmentVariable(TestHomeVariable, originalTestHome);

        }

    }

}
