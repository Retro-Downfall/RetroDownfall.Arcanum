using System.Text.RegularExpressions;

using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// A misspelled <c>{DynamicResource …}</c> key is not a build error and not a runtime exception:
/// Avalonia silently leaves the setter unapplied, so the control renders with the inherited value
/// and the defect only shows up as "that text looks wrong" on a screen nobody opens often. These
/// tests hold every first-party <c>Forge*</c> token referenced by Compendium markup to a definition
/// in the theme dictionaries. Keys owned by FluentTheme are out of scope — the app does not define
/// them and must not be forced to.
/// </summary>
public sealed class ThemeResourceReferenceTests
{

    private const string ForgeKeyPrefix = "Forge";

    private static readonly Regex ResourceReferencePattern = new(
        @"\{(?:Dynamic|Static)Resource\s+(?<key>[A-Za-z0-9_]+)\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex ResourceDefinitionPattern = new(
        @"x:Key=""(?<key>[A-Za-z0-9_]+)""",
        RegexOptions.Compiled);

    [Fact]
    public void Every_Forge_resource_key_referenced_by_markup_is_defined()
    {

        string markupRoot = CompendiumMarkupRoot();

        HashSet<string> defined = [];

        List<string> dangling = [];

        string[] markupFiles =
        [
            .. Directory.EnumerateFiles(markupRoot, "*.axaml", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(markupFiles);

        foreach (string file in markupFiles)
        {

            string markup = File.ReadAllText(file);

            foreach (Match match in ResourceDefinitionPattern.Matches(markup))
            {

                defined.Add(match.Groups["key"].Value);

            }

        }

        foreach (string file in markupFiles)
        {

            string markup = File.ReadAllText(file);

            foreach (Match match in ResourceReferencePattern.Matches(markup))
            {

                string key = match.Groups["key"].Value;

                if (!key.StartsWith(ForgeKeyPrefix, StringComparison.Ordinal) || defined.Contains(key))
                {

                    continue;

                }

                dangling.Add($"{Path.GetRelativePath(markupRoot, file)} → {key}");

            }

        }

        Assert.Empty(dangling);

    }

    /// <summary>
    /// The Familiar probe hands the operator a shell command to retype, so it has to be legible
    /// character by character: <c>l</c> against <c>1</c>, <c>0</c> against <c>O</c>. That is exactly
    /// what the proportional UI font blurs, so the remediation command binds the code font.
    /// </summary>
    [Fact]
    public void Familiar_remediation_command_renders_in_the_code_font()
    {

        string markup = File.ReadAllText(
            Path.Combine(CompendiumMarkupRoot(), "Views", "ProvidersPage.axaml"));

        int commandIndex = markup.IndexOf(
            "{Binding ProbeRemediationCommand}",
            StringComparison.Ordinal);

        Assert.True(commandIndex >= 0, "ProvidersPage no longer shows the probe remediation command.");

        int elementStart = markup.LastIndexOf('<', commandIndex);

        int elementEnd = markup.IndexOf('>', commandIndex);

        string element = markup[elementStart..elementEnd];

        Assert.Contains("{DynamicResource ForgeCodeFontFamily}", element, StringComparison.Ordinal);

    }

    private static string CompendiumMarkupRoot() =>
        Path.Combine(FindRepositoryRoot(), "src", "RetroDownfall.Compendium.Ux");

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
