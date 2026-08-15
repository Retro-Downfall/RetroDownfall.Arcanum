namespace RetroDownfall.Arcanum.Tests.NativeSqlCipher;

/// <summary>
/// Repository-relative locations of the hermetic SQLCipher delivery, resolved from the test
/// binary rather than the working directory so the same paths hold under <c>dotnet test</c>,
/// a published test host, and a coverage run.
/// </summary>
internal static class NativeSqlCipherTestPaths
{

    /// <summary>
    /// The runtime identifiers Arcanum ships a hermetic SQLCipher library for, in ordinal order.
    /// A RID outside this set has no native asset and must fail the build rather than fall back
    /// to an ambient library.
    /// </summary>
    internal static IReadOnlyList<string> ShippingRids { get; } =
    [
        "osx-arm64",

        "win-arm64",

        "win-x64",
    ];

    /// <summary>
    /// The exact library filename delivered for each shipping RID.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ExpectedOutputNames { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["osx-arm64"] = "libe_sqlcipher.dylib",

            ["win-arm64"] = "e_sqlcipher.dll",

            ["win-x64"] = "e_sqlcipher.dll",
        };

    /// <summary>
    /// Directory of the checked-in native asset project.
    /// </summary>
    internal static string AssetProject =>
        Path.Combine(RepositoryRoot(), "src", "RetroDownfall.Arcanum.NativeSqlCipher");

    /// <summary>
    /// The single source of provenance for every native binary Arcanum ships.
    /// </summary>
    internal static string Manifest =>
        Path.Combine(AssetProject, "native-source-manifest.json");

    /// <summary>
    /// Reproducible build entry point for one RID.
    /// </summary>
    internal static string BuildScript =>
        Path.Combine(RepositoryRoot(), "scripts", "build-native-sqlcipher.sh");

    /// <summary>
    /// Provenance, binary, and rebuild verifier.
    /// </summary>
    internal static string VerifyScript =>
        Path.Combine(RepositoryRoot(), "scripts", "verify-native-sqlcipher.sh");

    /// <summary>
    /// The project that consumes the native asset and must not reference an ambient bundle.
    /// </summary>
    internal static string InfrastructureProject => Path.Combine(
        RepositoryRoot(),
        "src",
        "RetroDownfall.Arcanum.Infrastructure",
        "RetroDownfall.Arcanum.Infrastructure.csproj");

    /// <summary>
    /// Absolute path of the native library checked in for <paramref name="rid" />, whether or not
    /// it exists yet.
    /// </summary>
    internal static string AssetPath(string rid) => Path.Combine(
        AssetProject,
        "runtimes",
        rid,
        "native",
        ExpectedOutputNames[rid]);

    /// <summary>
    /// Walks up from the test binary until it finds a directory holding both the solution file and
    /// <c>src</c>, so a partially matching ancestor cannot be mistaken for the repository root.
    /// </summary>
    internal static string RepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        List<string> inspected = [];

        while (directory is not null)
        {

            inspected.Add(directory.FullName);

            bool hasSolution = File.Exists(
                Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx"));

            bool hasSource = Directory.Exists(Path.Combine(directory.FullName, "src"));

            if (hasSolution && hasSource)
            {

                return directory.FullName;

            }

            if (inspected.Count > 32)
            {

                break;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException(
            "Could not locate the repository root (a directory holding both "
            + "RetroDownfall.Arcanum.slnx and src/) above "
            + AppContext.BaseDirectory
            + ". Inspected: "
            + string.Join(", ", inspected));

    }

}
