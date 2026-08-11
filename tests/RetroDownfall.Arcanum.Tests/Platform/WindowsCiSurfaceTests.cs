using System.Reflection;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Platform;

/// <summary>
/// Proves the Windows CI lane is genuinely exercising the Windows-gated surface.
/// </summary>
/// <remarks>
/// Every Windows-only test in this suite guards itself with
/// <c>Skip.IfNot(OperatingSystem.IsWindows(), …)</c>, which is correct on a developer's macOS
/// machine and useless as a signal: a lane that runs them all on Linux reports a clean green while
/// asserting nothing. That is not hypothetical — it is how <c>WindowsOsCredentialStore</c> shipped
/// persisting uninitialized heap instead of the operator's API key.
/// <para>
/// So the Windows lane sets <c>ARCANUM_REQUIRE_WINDOWS_SUITE=true</c>, and these facts turn a
/// silently-skipped lane into a red build. Mirrors the macOS lane's
/// <c>ARCANUM_REQUIRE_MACOS_WORKSPACE_CHECK</c> guard.
/// </para>
/// </remarks>
public sealed class WindowsCiSurfaceTests
{

    private const string RequireVariable = "ARCANUM_REQUIRE_WINDOWS_SUITE";

    private static bool WindowsLaneRequired() =>
        string.Equals(
            global::System.Environment.GetEnvironmentVariable(RequireVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The lane must actually be Windows. Setting the variable on a Linux runner by mistake would
    /// otherwise reproduce exactly the false confidence this guard exists to prevent.
    /// </summary>
    [Fact]
    public void Windows_ci_lane_runs_on_windows()
    {

        if (!WindowsLaneRequired())
        {
            return;
        }

        string platform = global::System.Environment.OSVersion.Platform.ToString();

        Assert.True(
            OperatingSystem.IsWindows(),
            $"{RequireVariable}=true but the suite is running on {platform}. "
            + "The Windows lane must run on a Windows runner or its platform-gated tests assert nothing.");

    }

    /// <summary>
    /// The OS credential store must be reachable, because it is the one Windows surface that had no
    /// coverage at all and shipped a defect because of it.
    /// </summary>
    [Fact]
    public void Windows_ci_lane_reaches_a_real_credential_backend()
    {

        if (!WindowsLaneRequired())
        {
            return;
        }

        OsCredentialStore store = new();

        Assert.True(
            store.IsAvailable,
            "The Windows lane requires a usable Credential Manager backend; without it the credential "
            + "round-trip tests skip and the lane proves nothing about secret storage.");

    }

    /// <summary>
    /// A count, not a list. Naming the individual tests would make this fail every time one is added
    /// or renamed; what matters is that the Windows-gated population has not silently gone to zero
    /// because someone deleted the last one or changed how they are gated.
    /// </summary>
    [Fact]
    public void Windows_gated_tests_still_exist_to_be_run()
    {

        if (!WindowsLaneRequired())
        {
            return;
        }

        int windowsGatedClasses = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Count(static type => type.Name.Contains("Windows", StringComparison.Ordinal)
                && type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Any(static method => method.GetCustomAttributes()
                        .Any(static attribute => attribute.GetType().Name is "FactAttribute"
                            or "SkippableFactAttribute"
                            or "TheoryAttribute"
                            or "SkippableTheoryAttribute")));

        Assert.True(
            windowsGatedClasses >= 3,
            $"Expected the Windows-specific test classes to still be present, found {windowsGatedClasses}. "
            + "If they were intentionally removed, lower this floor deliberately rather than deleting the guard.");

    }

}
