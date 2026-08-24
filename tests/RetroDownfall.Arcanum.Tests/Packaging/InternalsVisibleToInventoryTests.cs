using System.Xml.Linq;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// The exact set of assemblies each shipping project opens its internals to.
/// </summary>
/// <remarks>
/// An <c>InternalsVisibleTo</c> grant is a hole in the boundary that keeps a shipping assembly's
/// surface small, and it is invisible in every review that reads C# rather than MSBuild. Nothing
/// asserted the list, so a grant could be added to a production project and never be noticed again;
/// the Covenant benchmark host's two grants had already arrived that way. Pinning the set means
/// widening it is a deliberate edit to this file with a reason beside it.
/// </remarks>
public sealed class InternalsVisibleToInventoryTests
{

    [Theory]

    [InlineData(
        "src/RetroDownfall.Arcanum.Core/RetroDownfall.Arcanum.Core.csproj",
        "RetroDownfall.Arcanum.Covenant.Benchmarks,RetroDownfall.Arcanum.Tests")]

    [InlineData(
        "src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj",
        "RetroDownfall.Arcanum.Api,RetroDownfall.Arcanum.Api.DevHost,RetroDownfall.Arcanum.Cli,"
        + "RetroDownfall.Arcanum.Covenant.Benchmarks,RetroDownfall.Arcanum.RegexAotSmoke,"
        + "RetroDownfall.Arcanum.Tests")]

    [InlineData(
        "src/RetroDownfall.Arcanum.Secrets/RetroDownfall.Arcanum.Secrets.csproj",
        "RetroDownfall.Arcanum.Infrastructure,RetroDownfall.Arcanum.Tests,RetroDownfall.TheForge.Tests")]

    public void A_shipping_project_opens_its_internals_only_to_the_assemblies_named_here(
        string relativeProjectPath,
        string expected)
    {

        string projectPath = Path.GetFullPath(
            Path.Combine(FindRepositoryRoot(), relativeProjectPath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(File.Exists(projectPath), $"{relativeProjectPath} is missing.");

        IEnumerable<string> granted = XDocument.Load(projectPath)
            .Descendants("InternalsVisibleTo")
            .Select(static element => element.Attribute("Include")?.Value ?? string.Empty)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, string.Join(",", granted));

    }

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

}
