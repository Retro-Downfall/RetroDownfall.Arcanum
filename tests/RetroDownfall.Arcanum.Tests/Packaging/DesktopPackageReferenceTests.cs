using System.Globalization;

using System.Xml.Linq;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class DesktopPackageReferenceTests
{

    [Fact]
    public void Compendium_desktop_pins_one_avalonia_major_version()
    {

        IReadOnlyDictionary<string, string> references = ReadPackageReferences(
            Path.Combine(
                "src",
                "RetroDownfall.Compendium.Ux",
                "RetroDownfall.Compendium.Ux.csproj"));

        Assert.True(references.ContainsKey("Avalonia"), "Compendium.Ux must reference Avalonia.");

        int expectedMajor = MajorVersion(references["Avalonia"]);

        foreach (KeyValuePair<string, string> reference in references)
        {

            if (!reference.Key.StartsWith("Avalonia.", StringComparison.Ordinal))
            {

                continue;

            }

            Assert.True(
                MajorVersion(reference.Value) == expectedMajor,
                $"{reference.Key} {reference.Value} is pinned to a different Avalonia major version "
                + $"than Avalonia {references["Avalonia"]}.");

        }

    }

    private static int MajorVersion(string version) =>
        int.Parse(version.Split('.')[0], CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, string> ReadPackageReferences(string relativeProjectPath)
    {

        string path = Path.Combine(FindRepositoryRoot(), relativeProjectPath);

        Assert.True(File.Exists(path), $"Missing project file: {path}");

        Dictionary<string, string> references = new(StringComparer.Ordinal);

        foreach (XElement element in XDocument.Load(path).Descendants("PackageReference"))
        {

            string? id = element.Attribute("Include")?.Value;

            string? version = element.Attribute("Version")?.Value;

            if (id is null || version is null)
            {

                continue;

            }

            references[id] = version;

        }

        return references;

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
