using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Decides whether the Arcanum persistent paths currently resolve into the developer's real
/// profile directory, which no test may read or write.
/// </summary>
/// <remarks>
/// <see cref="ArcanumPaths"/> ignores <c>ARCANUM_TEST_HOME</c> unless a host environment reads
/// <c>Testing</c>, so a test run whose ambient environment is not <c>Testing</c> (the default for
/// <c>dotnet test</c>) resolves every path to the real directory. The comparison is against the
/// exact real directories and what lies beneath them, never against the profile as a prefix:
/// <see cref="Path.GetTempPath()"/> lives under <c>%USERPROFILE%</c> on Windows, so a temporary
/// test home is legitimately a descendant of the profile.
/// </remarks>
internal static class TestHomeGuard
{
    private const string ProfileConfigurationDirectory = ".config";

    private const string ApplicationDirectory = "arcanum";

    /// <summary>
    /// True when either persistent directory <see cref="ArcanumPaths"/> resolves right now is the
    /// developer's real one.
    /// </summary>
    internal static bool AmbientHomeIsUnredirected() =>
        IsUnderRealDirectory(
            ArcanumPaths.GrimoireDirectory,
            TestProcessPaths.OriginalUserProfile,
            TestProcessPaths.OriginalApplicationData)
        || IsUnderRealDirectory(
            ArcanumPaths.SecretStoreDirectory,
            TestProcessPaths.OriginalUserProfile,
            TestProcessPaths.OriginalApplicationData);

    /// <summary>
    /// True when <paramref name="path"/> is, or lies beneath, the real
    /// <c>&lt;profile&gt;/.config/arcanum</c> or <c>&lt;applicationData&gt;/arcanum</c> directory.
    /// </summary>
    internal static bool IsUnderRealDirectory(
        string path,
        string realUserProfile,
        string realApplicationData)
    {
        string candidate = Normalize(path);

        return (!string.IsNullOrEmpty(realUserProfile)
                && IsSameOrDescendant(
                    candidate,
                    Path.Combine(realUserProfile, ProfileConfigurationDirectory, ApplicationDirectory)))
            || (!string.IsNullOrEmpty(realApplicationData)
                && IsSameOrDescendant(
                    candidate,
                    Path.Combine(realApplicationData, ApplicationDirectory)));
    }

    private static bool IsSameOrDescendant(
        string candidate,
        string realDirectory)
    {
        string real = Normalize(realDirectory);

        // The separator on the boundary keeps `<real>-other` and `<real>2` from matching.
        return string.Equals(candidate, real, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                real + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(
        string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
