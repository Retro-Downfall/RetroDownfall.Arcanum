using System.Xml;

using System.Xml.Linq;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class MacOsBundleTemplateTests
{

    private const string CompendiumTemplate = "Info.plist.compendium";

    private const string TheForgeTemplate = "Info.plist.theforge";

    [Theory]

    [InlineData(CompendiumTemplate)]

    [InlineData(TheForgeTemplate)]

    public void Bundle_template_declares_the_cocoa_high_resolution_key(string templateFileName)
    {

        IReadOnlyDictionary<string, string> entries = ReadTemplate(templateFileName);

        Assert.True(
            entries.ContainsKey("NSHighResolutionCapable"),
            $"{templateFileName} must declare the documented Cocoa key NSHighResolutionCapable.");

        Assert.Equal("true", entries["NSHighResolutionCapable"]);

        Assert.DoesNotContain(
            entries.Keys,
            key => key.Contains("HighResolution", StringComparison.Ordinal)
                && !string.Equals(key, "NSHighResolutionCapable", StringComparison.Ordinal));

    }

    [Fact]
    public void Bundle_templates_declare_the_same_keys()
    {

        IReadOnlyDictionary<string, string> compendium = ReadTemplate(CompendiumTemplate);

        IReadOnlyDictionary<string, string> theForge = ReadTemplate(TheForgeTemplate);

        Assert.Equal(
            compendium.Keys.Order(StringComparer.Ordinal),
            theForge.Keys.Order(StringComparer.Ordinal));

    }

    private static IReadOnlyDictionary<string, string> ReadTemplate(string templateFileName)
    {

        string path = Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "packaging",
            "macos",
            templateFileName);

        Assert.True(File.Exists(path), $"Missing macOS bundle template: {path}");

        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

        using XmlReader reader = XmlReader.Create(path, settings);

        XElement dictionary = XDocument.Load(reader).Root?.Element("dict")
            ?? throw new InvalidOperationException($"{templateFileName} has no top-level <dict>.");

        Dictionary<string, string> entries = new(StringComparer.Ordinal);

        string? pendingKey = null;

        foreach (XElement element in dictionary.Elements())
        {

            if (string.Equals(element.Name.LocalName, "key", StringComparison.Ordinal))
            {

                pendingKey = element.Value;

                continue;

            }

            Assert.NotNull(pendingKey);

            entries[pendingKey] = string.Equals(element.Name.LocalName, "string", StringComparison.Ordinal)
                ? element.Value
                : element.Name.LocalName;

            pendingKey = null;

        }

        Assert.Null(pendingKey);

        return entries;

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
