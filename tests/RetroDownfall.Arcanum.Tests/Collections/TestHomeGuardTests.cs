using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Collections;

/// <summary>
/// The predicate that decides whether a test run is about to touch the developer's real profile.
/// Pure path arithmetic over fixture directories, so it mutates no process state.
/// </summary>
public sealed class TestHomeGuardTests
{
    private static readonly string Profile = Path.Combine(
        Path.GetTempPath(),
        "test-home-guard-fixture",
        "profile");

    private static readonly string ApplicationData = Path.Combine(
        Profile,
        "AppData",
        "Roaming");

    private static readonly string RealConfigDirectory = Path.Combine(
        Profile,
        ".config",
        "arcanum");

    private static readonly string RealApplicationDataDirectory = Path.Combine(
        ApplicationData,
        "arcanum");

    [Fact]
    public void Guard_accepts_a_temp_root_under_the_profile_and_rejects_the_real_config_directory()
    {
        // Path.GetTempPath() lives under %USERPROFILE% on Windows, so a temporary test home is a
        // legitimate descendant of the profile and must not be mistaken for the real directory.
        string tempHome = Path.Combine(Profile, "AppData", "Local", "Temp", "arcanum-test-abc");

        Assert.False(IsReal(Path.Combine(tempHome, ".config", "arcanum")));

        Assert.False(IsReal(tempHome));

        Assert.True(IsReal(RealConfigDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("logs")]
    [InlineData("keys/nested")]
    public void Guard_rejects_the_real_directories_and_everything_beneath_them(
        string relative)
    {
        Assert.True(IsReal(Path.Combine(RealConfigDirectory, relative)));

        Assert.True(IsReal(Path.Combine(RealApplicationDataDirectory, relative)));
    }

    [Fact]
    public void Guard_ignores_a_trailing_separator_on_the_real_directory()
    {
        Assert.True(IsReal(RealConfigDirectory + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData(".config", "arcanum-other")]
    [InlineData(".config", "arcanum2")]
    [InlineData(".config", "")]
    [InlineData("", "")]
    public void Guard_accepts_siblings_and_ancestors_of_the_real_directory(
        string parent,
        string leaf)
    {
        Assert.False(IsReal(Path.Combine(Profile, parent, leaf)));
    }

    private static bool IsReal(
        string path) =>
        TestHomeGuard.IsUnderRealDirectory(path, Profile, ApplicationData);
}
