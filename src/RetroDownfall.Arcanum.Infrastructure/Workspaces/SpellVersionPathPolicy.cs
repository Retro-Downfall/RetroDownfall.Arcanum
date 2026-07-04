using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

/// <summary>
/// Central authority for spell version label validation and label &lt;-&gt; filename mapping.
/// Version labels are string-based (alphanumeric and dots only) and map onto sidecar
/// <c>SPELL.v{label}.md</c> files alongside the active <c>SPELL.md</c>. Replaces the earlier
/// ad-hoc integer <c>VersionFileRegex</c> that lived in <c>SpellExecutionEndpoints</c>.
/// </summary>
internal static partial class SpellVersionPathPolicy
{

    internal static bool IsValidLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label) && LabelRegex().IsMatch(label);

    internal static string BuildVersionFileName(string label) => $"SPELL.v{label}.md";

    internal static bool TryParseLabelFromFileName(string fileName, out string label)
    {

        Match match = VersionFileRegex().Match(fileName);

        if (!match.Success)
        {

            label = string.Empty;

            return false;

        }

        label = match.Groups[1].Value;

        return true;

    }

    [GeneratedRegex(@"^[A-Za-z0-9.]+$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^SPELL\.v([A-Za-z0-9.]+)\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionFileRegex();

}
