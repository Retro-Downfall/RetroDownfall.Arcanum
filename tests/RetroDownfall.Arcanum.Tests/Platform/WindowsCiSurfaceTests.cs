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

    private const string CredentialStoreVariable = "ARCANUM_TEST_OS_CREDENTIAL_STORE";

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
    /// <remarks>
    /// On Windows <c>IsAvailable</c> is a constant — Credential Manager is part of the OS and has no
    /// separate service that can be missing — so asserting it alone is an assertion that cannot fail.
    /// A read of an account nobody has written answers the question the lane actually cares about:
    /// <c>CredReadW</c> reached the backend and the backend said the account is not there. It stays
    /// read-only, so unlike the round-trip suite it never touches the runner's stored credentials.
    /// </remarks>
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

        OsCredentialStoreResult read = store.TryGet($"arcanum-ci-{Guid.NewGuid():N}", "never-written");

        Assert.True(
            read.Status == OsCredentialStoreStatus.NotFound,
            "Credential Manager must answer a read of an unwritten account with NotFound. "
            + $"It answered {read.Status}: {read.Message}");

    }

    /// <summary>
    /// The round-trip against the real Credential Manager is the assertion this lane exists for, and it
    /// gates itself on <c>ARCANUM_TEST_OS_CREDENTIAL_STORE</c> <em>before</em> it gates on the platform.
    /// That variable is set in exactly one place — <c>.github/workflows/ci.yml</c> — and was asserted
    /// nowhere: delete or mistype that line and every real-backend credential round trip skips in
    /// silence while this class, and the whole Windows lane, still reports green. That is precisely the
    /// false confidence the lane was built to remove, so the opt-in is part of the contract.
    /// </summary>
    [Fact]
    public void Windows_ci_lane_opts_in_to_the_real_credential_store_round_trip()
    {

        if (!WindowsLaneRequired())
        {
            return;
        }

        string? optIn = global::System.Environment.GetEnvironmentVariable(CredentialStoreVariable);

        Assert.True(
            string.Equals(optIn, "true", StringComparison.OrdinalIgnoreCase),
            $"{RequireVariable}=true but {CredentialStoreVariable} is '{optIn ?? "<unset>"}'. "
            + "OsCredentialStoreRoundTripTests skips on that variable before it checks the platform, so "
            + "without it the lane performs no round trip against a real OS secret store.");

    }

    /// <summary>
    /// A count, not a list. Naming the individual tests would make this fail every time one is added
    /// or renamed; what matters is that the Windows-gated population has not silently gone to zero
    /// because someone deleted the last one or changed how they are gated.
    /// </summary>
    /// <remarks>
    /// Two corrections to an earlier form of this guard. It counted <see cref="WindowsCiSurfaceTests"/>
    /// itself, so its floor of three was really a floor of two external classes. And most classes whose
    /// name contains "Windows" are not platform-gated at all — they drive fakes and pure logic and run
    /// identically on every OS — so the count could stay above the floor while the only genuinely
    /// skip-gated class was deleted, which is the one removal the guard is named for.
    /// </remarks>
    [Fact]
    public void Windows_gated_tests_still_exist_to_be_run()
    {

        if (!WindowsLaneRequired())
        {
            return;
        }

        Type[] windowsTestClasses = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(static type => type != typeof(WindowsCiSurfaceTests))
            .Where(static type => type.Name.Contains("Windows", StringComparison.Ordinal))
            .Where(static type => TestMethods(type).Length > 0)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            windowsTestClasses.Length >= 3,
            "Expected the Windows-specific test classes to still be present, found "
            + $"{windowsTestClasses.Length}: [{string.Join(", ", windowsTestClasses.Select(static type => type.Name))}]. "
            + "If they were intentionally removed, lower this floor deliberately rather than deleting the guard.");

        Type[] skipGated = windowsTestClasses
            .Where(static type => TestMethods(type).Any(IsSkippable))
            .ToArray();

        Assert.True(
            skipGated.Length >= 1,
            "Every remaining Windows-named test class runs identically on macOS and Linux, so nothing in "
            + "this assembly is actually gated on the platform any more and this lane exercises no "
            + "Windows-only code path. Restore the skip-gated class or retire this lane deliberately.");

    }

    private static MethodInfo[] TestMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static method => method.GetCustomAttributes()
                .Any(static attribute => attribute.GetType().Name is "FactAttribute"
                    or "SkippableFactAttribute"
                    or "TheoryAttribute"
                    or "SkippableTheoryAttribute"))
            .ToArray();

    /// <summary>
    /// A <c>[Skippable*]</c> attribute is the only platform gating this assembly can see by reflection:
    /// <c>Skip.IfNot(OperatingSystem.IsWindows(), …)</c> lives in the method body, but the attribute it
    /// requires is on the signature.
    /// </summary>
    private static bool IsSkippable(MethodInfo method) =>
        method.GetCustomAttributes()
            .Any(static attribute => attribute.GetType().Name
                is "SkippableFactAttribute" or "SkippableTheoryAttribute");

}
