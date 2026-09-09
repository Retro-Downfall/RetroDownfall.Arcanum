using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class TestCredentialStorePolicyTests
{
    private const string OptInVariable = "ARCANUM_TEST_IN_MEMORY_CREDENTIALS";

    private const string TestingEnvironment = "Testing";

    private const string DotnetEnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void In_memory_store_requires_every_test_isolation_gate(
        bool testingEnvironment,
        bool isolatedHome,
        bool exactOptIn)
    {
        string testHome = CreateTestHome();

        try
        {
            Type selectedType = ResolveCredentialStoreType(
                testingEnvironment ? TestingEnvironment : Environments.Production,
                isolatedHome ? testHome : null,
                exactOptIn ? "1" : "true");

            Type expectedType = testingEnvironment && isolatedHome && exactOptIn
                ? typeof(InMemoryOsCredentialStore)
                : typeof(OsCredentialStore);

            Assert.Equal(expectedType, selectedType);
        }
        finally
        {
            Directory.Delete(testHome, recursive: true);
        }
    }

    [Theory]
    [InlineData("testing", "1")]
    [InlineData("Testing ", "1")]
    [InlineData("Testing", "true")]
    [InlineData("Testing", " 1")]
    public void Gate_values_are_exact(string environmentName, string optIn)
    {
        string testHome = CreateTestHome();

        try
        {
            Type selectedType = ResolveCredentialStoreType(environmentName, testHome, optIn);

            Assert.Equal(typeof(OsCredentialStore), selectedType);
        }
        finally
        {
            Directory.Delete(testHome, recursive: true);
        }
    }

    [Fact]
    public void Test_home_must_be_an_existing_isolated_child_of_the_test_temp_root()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "arcanum-tests");
        string missingHome = Path.Combine(testRoot, $"missing-{Guid.NewGuid():N}");
        string outsideHome = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideHome);

        try
        {
            Assert.Equal(
                typeof(OsCredentialStore),
                ResolveCredentialStoreType(TestingEnvironment, testRoot, "1"));

            Assert.Equal(
                typeof(OsCredentialStore),
                ResolveCredentialStoreType(TestingEnvironment, missingHome, "1"));

            Assert.Equal(
                typeof(OsCredentialStore),
                ResolveCredentialStoreType(TestingEnvironment, outsideHome, "1"));
        }
        finally
        {
            Directory.Delete(outsideHome, recursive: true);
        }
    }

    [Fact]
    public void In_memory_store_never_persists_credential_plaintext()
    {
        string testHome = CreateTestHome();

        try
        {
            IOsCredentialStore store = ResolveCredentialStore(
                TestingEnvironment,
                testHome,
                "1");

            const string secret = "published-smoke-secret";

            Assert.Equal(
                OsCredentialStoreStatus.Ok,
                store.Set("arcanum-test", "smoke", secret).Status);

            Assert.Empty(Directory.EnumerateFiles(testHome, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(testHome, recursive: true);
        }
    }

    [Fact]
    public void Pre_di_startup_probe_honors_the_same_isolated_test_credential_boundary()
    {
        string testHome = CreateTestHome();
        string? originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");
        string? originalOptIn = global::System.Environment.GetEnvironmentVariable(OptInVariable);
        string? originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable(
            DotnetEnvironmentVariable);
        string? originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable(
            AspNetCoreEnvironmentVariable);

        try
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", testHome);
            global::System.Environment.SetEnvironmentVariable(OptInVariable, "1");
            global::System.Environment.SetEnvironmentVariable(
                DotnetEnvironmentVariable,
                TestingEnvironment);
            global::System.Environment.SetEnvironmentVariable(
                AspNetCoreEnvironmentVariable,
                TestingEnvironment);

            InstallationStartupProbe probe = InstallationStartupProbe.CreateDefault();

            Assert.IsType<InMemoryOsCredentialStore>(probe.CredentialStore);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", originalTestHome);
            global::System.Environment.SetEnvironmentVariable(OptInVariable, originalOptIn);
            global::System.Environment.SetEnvironmentVariable(
                DotnetEnvironmentVariable,
                originalDotnetEnvironment);
            global::System.Environment.SetEnvironmentVariable(
                AspNetCoreEnvironmentVariable,
                originalAspNetCoreEnvironment);
            Directory.Delete(testHome, recursive: true);
        }
    }

    [Theory]
    [InlineData("Testing", null)]
    [InlineData(null, "Testing")]
    [InlineData("Testing", "Production")]
    [InlineData("Production", "Testing")]
    public void Pre_di_test_credential_boundary_requires_matching_explicit_environments(
        string? dotnetEnvironment,
        string? aspNetCoreEnvironment)
    {
        string testHome = CreateTestHome();
        string? originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");
        string? originalOptIn = global::System.Environment.GetEnvironmentVariable(OptInVariable);
        string? originalDotnetEnvironment = global::System.Environment.GetEnvironmentVariable(
            DotnetEnvironmentVariable);
        string? originalAspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable(
            AspNetCoreEnvironmentVariable);

        try
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", testHome);
            global::System.Environment.SetEnvironmentVariable(OptInVariable, "1");
            global::System.Environment.SetEnvironmentVariable(
                DotnetEnvironmentVariable,
                dotnetEnvironment);
            global::System.Environment.SetEnvironmentVariable(
                AspNetCoreEnvironmentVariable,
                aspNetCoreEnvironment);

            InstallationStartupProbe probe = InstallationStartupProbe.CreateDefault();

            Assert.IsType<OsCredentialStore>(probe.CredentialStore);
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", originalTestHome);
            global::System.Environment.SetEnvironmentVariable(OptInVariable, originalOptIn);
            global::System.Environment.SetEnvironmentVariable(
                DotnetEnvironmentVariable,
                originalDotnetEnvironment);
            global::System.Environment.SetEnvironmentVariable(
                AspNetCoreEnvironmentVariable,
                originalAspNetCoreEnvironment);
            Directory.Delete(testHome, recursive: true);
        }
    }

    private static Type ResolveCredentialStoreType(
        string environmentName,
        string? testHome,
        string? optIn) =>
        ResolveCredentialStore(environmentName, testHome, optIn).GetType();

    private static IOsCredentialStore ResolveCredentialStore(
        string environmentName,
        string? testHome,
        string? optIn)
    {
        string? originalTestHome = global::System.Environment.GetEnvironmentVariable("ARCANUM_TEST_HOME");
        string? originalOptIn = global::System.Environment.GetEnvironmentVariable(OptInVariable);

        try
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", testHome);
            global::System.Environment.SetEnvironmentVariable(OptInVariable, optIn);

            ServiceCollection services = new();
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
            services.AddArcanumSecretStore();

            using ServiceProvider provider = services.BuildServiceProvider();

            return provider.GetRequiredService<IOsCredentialStore>();
        }
        finally
        {
            global::System.Environment.SetEnvironmentVariable("ARCANUM_TEST_HOME", originalTestHome);
            global::System.Environment.SetEnvironmentVariable(OptInVariable, originalOptIn);
        }
    }

    private static string CreateTestHome()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "arcanum-tests",
            $"credential-policy-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        return path;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "ArcanumCredentialPolicyTests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
