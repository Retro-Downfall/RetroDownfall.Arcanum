using System.Text;

using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// One authored source file, with its comments removed.
/// </summary>
internal readonly record struct ProductionSource(string RelativePath, string Text)
{

    /// <summary>Reports whether this file's code names the supplied construct.</summary>
    internal bool Names(string construct) => Text.Contains(construct, StringComparison.Ordinal);

    /// <summary>
    /// Reports whether this file carries exactly the supplied name.
    /// </summary>
    /// <remarks>
    /// Whole-name rather than a suffix. A suffix match would exempt <c>MacOsCredentialStore.cs</c> for
    /// an inventory that only meant to exempt <c>OsCredentialStore.cs</c>, and an inventory whose
    /// exemptions are wider than they read is one that stops reporting the site it was written for.
    /// </remarks>
    internal bool Is(string fileName) =>
        string.Equals(Path.GetFileName(RelativePath), fileName, StringComparison.Ordinal);

    /// <summary>
    /// Reports whether this file is the exact normalized repository-relative owner.
    /// </summary>
    internal bool IsExactOwner(string repositoryRelativePath) =>
        string.Equals(
            NormalizeRepositoryRelativePath(RelativePath),
            NormalizeRepositoryRelativePath(repositoryRelativePath),
            StringComparison.Ordinal);

    private static string NormalizeRepositoryRelativePath(string path) =>
        path.Replace('\\', '/');

}

/// <summary>
/// The authored production sources, for the suites that assert who may call what.
/// </summary>
/// <remarks>
/// C# has no friend classes, so a rule like "only this type may derive that identity" is a property of
/// the call graph rather than of a visibility modifier. Suites that pin such a rule are inventory
/// assertions over production source rather than behavior tests, because the failure they prevent is a
/// new call site, not a wrong result.
///
/// <para>Shared rather than copied into each suite. Two scanners that agree on the day they are written
/// drift as soon as either learns about a new build-output directory, and the one that stops excluding
/// generated intermediates starts reporting offenders nobody authored.</para>
/// </remarks>
internal static class ProductionSourceInventory
{

    /// <summary>
    /// Every authored <c>.cs</c> file under <c>src</c>, comment-free and repository-relative.
    /// </summary>
    internal static IReadOnlyList<ProductionSource> Sources() => Under("src");

    /// <summary>
    /// Every authored <c>.cs</c> file under <c>tests</c>, comment-free and repository-relative.
    /// </summary>
    /// <remarks>
    /// The same scanner pointed at the suites, for the rules that are properties of the test call
    /// graph rather than of production's. Which production entry point a test enters through cannot be
    /// read from a type's shape at all — only from what the test source names — and a second scanner
    /// written for that reading would drift from this one the first time either learned about a new
    /// intermediate directory.
    /// </remarks>
    internal static IReadOnlyList<ProductionSource> TestSuiteSources() => Under("tests");

    private static IReadOnlyList<ProductionSource> Under(string repositoryRelativeDirectory)
    {

        string repositoryRoot = NativeSqlCipherTestPaths.RepositoryRoot();

        string root = Path.Combine(repositoryRoot, repositoryRelativeDirectory);

        List<ProductionSource> sources = [];

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {

            // Generated intermediates are build output, not authored production code.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {

                continue;

            }

            sources.Add(new ProductionSource(
                Path.GetRelativePath(repositoryRoot, file),
                WithoutComments(File.ReadAllText(file))));

        }

        Assert.NotEmpty(sources);

        return sources;

    }

    /// <summary>
    /// Removes comments so an inventory matches on code alone.
    /// </summary>
    /// <remarks>
    /// Without this, documenting why a construct is restricted trips the very guard that restricts it —
    /// which would push the explanation out of the code and leave the next reader with a rule and no
    /// reason. Line-oriented on purpose: it drops whole comment lines and block-comment bodies, and
    /// deliberately does not attempt to parse trailing comments out of lines that also contain code,
    /// because under-stripping only risks a false positive that a human will read, never a miss.
    /// </remarks>
    private static string WithoutComments(string text)
    {

        StringBuilder stripped = new(text.Length);

        bool inBlockComment = false;

        foreach (string line in text.Split('\n'))
        {

            string trimmed = line.TrimStart();

            if (inBlockComment)
            {

                if (trimmed.Contains("*/", StringComparison.Ordinal))
                {

                    inBlockComment = false;

                }

                continue;

            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {

                inBlockComment = !trimmed.Contains("*/", StringComparison.Ordinal);

                continue;

            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {

                continue;

            }

            _ = stripped.Append(line).Append('\n');

        }

        return stripped.ToString();

    }

}
