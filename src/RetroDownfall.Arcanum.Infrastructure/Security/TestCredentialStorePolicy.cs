using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Selects an in-memory credential store only for explicitly isolated executable smoke tests.
/// </summary>
internal static class TestCredentialStorePolicy
{
    private const string TestHomeVariable = "ARCANUM_TEST_HOME";

    private const string InMemoryOptInVariable = "ARCANUM_TEST_IN_MEMORY_CREDENTIALS";

    private const string TestingEnvironment = "Testing";

    private const string DotnetEnvironmentVariable = "DOTNET_ENVIRONMENT";

    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    internal static IOsCredentialStore Create(IServiceProvider services)
    {
        string? environmentName = services.GetService<IHostEnvironment>()?.EnvironmentName;

        return CreateForEnvironment(environmentName);
    }

    /// <summary>
    /// Applies the same isolated-test boundary before the CLI has built its dependency container.
    /// Both host-environment variables must agree exactly so a partial or ambiguous test launch
    /// cannot replace the operating-system credential store.
    /// </summary>
    internal static IOsCredentialStore CreateForCurrentProcess()
    {
        string? dotnetEnvironment = global::System.Environment.GetEnvironmentVariable(
            DotnetEnvironmentVariable);
        string? aspNetCoreEnvironment = global::System.Environment.GetEnvironmentVariable(
            AspNetCoreEnvironmentVariable);
        string? environmentName = string.Equals(
            dotnetEnvironment,
            aspNetCoreEnvironment,
            StringComparison.Ordinal)
                ? dotnetEnvironment
                : null;

        return CreateForEnvironment(environmentName);
    }

    private static IOsCredentialStore CreateForEnvironment(string? environmentName)
    {
        string? testHome = global::System.Environment.GetEnvironmentVariable(TestHomeVariable);
        string? optIn = global::System.Environment.GetEnvironmentVariable(InMemoryOptInVariable);

        return IsEnabled(environmentName, testHome, optIn)
            ? new InMemoryOsCredentialStore()
            : new OsCredentialStore();
    }

    internal static bool IsEnabled(
        string? environmentName,
        string? testHome,
        string? optIn) =>
        IsEnabled(environmentName, testHome, optIn, Path.GetTempPath());

    internal static bool IsEnabled(
        string? environmentName,
        string? testHome,
        string? optIn,
        string? processTemporaryDirectory)
    {
        if (!string.Equals(environmentName, TestingEnvironment, StringComparison.Ordinal)
            || !string.Equals(optIn, "1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(testHome)
            || !Path.IsPathFullyQualified(testHome)
            || string.IsNullOrWhiteSpace(processTemporaryDirectory)
            || !Path.IsPathFullyQualified(processTemporaryDirectory))
        {
            return false;
        }

        try
        {
            string testRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(processTemporaryDirectory, "arcanum-tests")));

            string resolvedHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(testHome));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!Directory.Exists(resolvedHome)
                || string.Equals(resolvedHome, testRoot, comparison)
                || !resolvedHome.StartsWith(testRoot + Path.DirectorySeparatorChar, comparison))
            {
                return false;
            }

            DirectoryInfo? current = new(resolvedHome);

            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    testRoot,
                    comparison))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return false;
        }
    }
}
