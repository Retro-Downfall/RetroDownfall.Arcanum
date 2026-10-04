using System.Xml.Linq;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A transitive-dependency pin that holds for some projects and not others protects nothing: the
/// project that missed it resolves the vulnerable or incompatible version on its own.
/// </summary>
public sealed class PackageVersionConsistencyTests
{
    private const string PinnedPackage = "Microsoft.Bcl.Memory";

    /// <summary>
    /// <c>Microsoft.Bcl.Memory</c> is pinned to defend the <c>Microsoft.ML.Tokenizers.Data.O200kBase</c>
    /// netstandard2.0 shim path. Every project under <c>src/</c> and <c>tests/</c> has to resolve the
    /// same version, whether through the solution-wide reference or a per-project override.
    /// </summary>
    [Fact]
    public void Every_project_resolves_the_same_bcl_memory_version()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string baseline = Assert.Single(
            ReferencedVersions(Path.Combine(root, "Directory.Build.props")));

        Dictionary<string, string> resolved = [];

        foreach (string directory in new[] { "src", "tests" })
        {
            foreach (string project in Directory.EnumerateFiles(Path.Combine(root, directory), "*.csproj", SearchOption.AllDirectories))
            {
                if (project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || project.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                // The last Update or Include in the project wins over the solution-wide reference.
                string effective = ReferencedVersions(project).LastOrDefault() ?? baseline;

                resolved[Path.GetRelativePath(root, project)] = effective;
            }
        }

        Assert.True(resolved.Count > 10, "The scan did not find the solution's projects, so it proved nothing.");

        Assert.True(
            resolved.Values.Distinct(StringComparer.Ordinal).Count() == 1,
            $"{PinnedPackage} resolves to different versions across projects:"
            + global::System.Environment.NewLine
            + string.Join(
                global::System.Environment.NewLine,
                resolved.Select(static pair => $"{pair.Key}: {pair.Value}")));
    }

    private static IReadOnlyList<string> ReferencedVersions(string projectOrProps) =>
        XDocument.Load(projectOrProps)
            .Descendants()
            .Where(static element => element.Name.LocalName == "PackageReference")
            .Where(static element => string.Equals((string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"), PinnedPackage, StringComparison.Ordinal))
            .Select(static element => (string?)element.Attribute("Version"))
            .OfType<string>()
            .ToArray();
}
