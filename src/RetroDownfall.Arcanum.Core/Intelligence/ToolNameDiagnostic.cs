using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ToolNameDiagnosticKind>))]
public enum ToolNameDiagnosticKind
{
    [JsonStringEnumMemberName("removed")]
    Removed,

    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

public sealed record ToolNameDiagnostic(string ToolName, ToolNameDiagnosticKind Kind, string? Replacement, string Message);

/// <summary>Static parsing diagnoses only removed names; unknown and unavailable require live discovery.</summary>
public static class ToolNameDiagnostics
{
    public static ToolNameDiagnostic[] Removed(IEnumerable<string>? declarations) => Diagnose(declarations, null);

    public static ToolNameDiagnostic[] Diagnose(IEnumerable<string>? declarations, IEnumerable<string>? availableNames)
    {
        if (declarations is null) return [];

        HashSet<string>? available = availableNames is null ? null : new(availableNames, StringComparer.OrdinalIgnoreCase);

        List<ToolNameDiagnostic> diagnostics = [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in declarations)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;

            if (ArcanumToolNameCatalog.TryGetReplacement(name, out string? replacement))
            {
                diagnostics.Add(new(name, ToolNameDiagnosticKind.Removed, replacement,
                    $"Declared tool '{name}' was removed; use '{replacement}' in a corrected declaration. Removed names cannot execute."));
            }
            else if (available is not null && !available.Contains(name)
                && !available.Contains(ArcanumBuiltInToolNames.Canonicalize(name)))
            {
                bool known = ArcanumToolNameCatalog.IsKnown(name);

                diagnostics.Add(new(name, known ? ToolNameDiagnosticKind.Unavailable : ToolNameDiagnosticKind.Unknown, null,
                    known ? $"Declared tool '{name}' is known but unavailable in the current tool surface."
                        : $"Declared tool '{name}' is unknown in the current tool surface."));
            }
        }

        return diagnostics.ToArray();
    }
}
