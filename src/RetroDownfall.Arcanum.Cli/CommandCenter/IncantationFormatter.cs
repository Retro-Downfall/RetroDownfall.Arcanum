using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Formats <see cref="IncantationRecord"/> into ≤3 display lines (separator is external).
/// Fail-closed heavy/sensitive suppression; never parses preformatted Tool: display strings.
/// </summary>
internal static partial class IncantationFormatter
{
    public const int MaxContentLines = 3;

    public const string Ellipsis = "…";

    private static readonly HashSet<string> HeavyToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "write",
        "search_replace",
        "str_replace",
        "apply_patch",
        "edit_notebook",
        "EditNotebook",
        "Write",
        "StrReplace",
    };

    private static readonly HashSet<string> SensitiveKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "content",
        "body",
        "text",
        "data",
        "bytes",
        "base64",
        "patch",
        "diff",
        "old_string",
        "oldString",
        "new_string",
        "newString",
        "replacement",
        "contents",
        "file_text",
        "fileText",
        "input",
        "payload",
        "source",
        "code",
        "prompt",
        "api_key",
        "apiKey",
        "token",
        "password",
        "secret",
        "authorization",
        "auth",
        "access_token",
        "accessToken",
        "refresh_token",
        "refreshToken",
        "bearer",
        "client_secret",
        "clientSecret",
    };

    private static readonly HashSet<string> SummaryKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path",
        "file",
        "file_path",
        "filePath",
        "filename",
        "name",
        "key",
        "command",
        "cmd",
        "url",
        "uri",
        "query",
        "pattern",
        "glob",
        "target",
        "directory",
        "dir",
        "cwd",
        "working_directory",
        "workingDirectory",
    };

    public static string SeparatorLine(int contentWidth)
    {
        int w = Math.Max(1, contentWidth);
        return new string('─', w);
    }

    public static IReadOnlyList<string> FormatBlock(IncantationRecord record, int contentWidth)
    {
        ArgumentNullException.ThrowIfNull(record);
        int width = Math.Max(1, contentWidth);

        if (!string.IsNullOrEmpty(record.SafeSummaryOverride))
        {
            return CapToThreeLines(Sanitize(record.SafeSummaryOverride), width);
        }

        string stateLabel = record.State switch
        {
            IncantationState.Pending => "pending",
            IncantationState.Succeeded => "ok",
            IncantationState.Failed => "failed",
            _ => "unknown",
        };

        string header = $"{record.ToolName} [{stateLabel}]";
        bool heavy = IsHeavyTool(record.ToolName)
            || HasSensitiveOrContentBearingArgs(record.ArgumentsJson)
            || HasSensitiveOrContentBearingArgs(record.ResultText);

        List<string> bodyParts = new();
        if (heavy || !TryParseObject(record.ArgumentsJson, out JsonElement args))
        {
            if (heavy)
            {
                string? summary = BuildSafeSummary(record.ArgumentsJson);
                if (!string.IsNullOrEmpty(summary))
                {
                    bodyParts.Add(summary);
                }
            }
            else if (!string.IsNullOrWhiteSpace(record.ArgumentsJson))
            {
                // Malformed JSON → name/state only (no raw blob).
            }
        }
        else if (!heavy)
        {
            string? summary = BuildSafeSummaryFromElement(args);
            if (!string.IsNullOrEmpty(summary))
            {
                bodyParts.Add(summary);
            }
            else if (IsObjectEmptyOrOnlySensitive(args))
            {
                // nothing safe to show
            }
            else
            {
                // Non-heavy with only non-summary keys still present — fail closed to name/state.
            }
        }

        if (record.State == IncantationState.Failed && !string.IsNullOrWhiteSpace(record.ErrorText))
        {
            string err = Sanitize(record.ErrorText!);
            if (!heavy && !LooksLikeHugeBlob(err))
            {
                bodyParts.Add("error: " + TruncateCells(err, Math.Max(8, width * 2)));
            }
            else
            {
                bodyParts.Add("error");
            }
        }
        else if (record.State == IncantationState.Succeeded
                 && !heavy
                 && !string.IsNullOrWhiteSpace(record.ResultText)
                 && !LooksLikeHugeBlob(record.ResultText!))
        {
            bodyParts.Add("→ " + TruncateCells(Sanitize(record.ResultText!), Math.Max(8, width * 2)));
        }

        if (record.WardNotes.Count > 0)
        {
            bodyParts.Add(Sanitize(record.WardNotes[^1]));
        }

        string combined = bodyParts.Count == 0
            ? header
            : header + " — " + string.Join(" · ", bodyParts);

        return CapToThreeLines(Sanitize(combined), width);
    }

    public static bool IsHeavyTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && HeavyToolNames.Contains(toolName.Trim());

    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        string k = key.Trim();
        if (SensitiveKeyNames.Contains(k))
        {
            return true;
        }

        // Heuristic: keys that embed sensitive tokens.
        return SensitiveKeyRegex().IsMatch(k);
    }

    public static bool IsSummaryKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && SummaryKeyNames.Contains(key.Trim());

    internal static IReadOnlyList<string> CapToThreeLines(string text, int width)
    {
        List<string> wrapped = WrapToCellWidth(text, width).ToList();
        if (wrapped.Count <= MaxContentLines)
        {
            return wrapped;
        }

        List<string> kept = wrapped.Take(MaxContentLines).ToList();
        string last = kept[^1];
        kept[^1] = FitEllipsis(last, width);
        return kept;
    }

    internal static string FitEllipsis(string line, int width)
    {
        string sanitized = Sanitize(line);
        int ellipsisWidth = ComposerLayout.MeasureCellWidth(Ellipsis);
        int budget = Math.Max(0, width - ellipsisWidth);
        if (budget <= 0)
        {
            return TruncateCells(Ellipsis, width);
        }

        string head = TruncateCells(sanitized, budget);
        return head + Ellipsis;
    }

    internal static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder sb = new(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value == '\t')
            {
                int pad = ComposerLayout.TabStop - (ComposerLayout.MeasureCellWidth(sb.ToString()) % ComposerLayout.TabStop);
                if (pad <= 0 || pad > ComposerLayout.TabStop)
                {
                    pad = ComposerLayout.TabStop;
                }

                _ = sb.Append(' ', pad);
                continue;
            }

            if (value is '\r' or '\n')
            {
                _ = sb.Append(' ');
                continue;
            }

            // Strip C0/C1 controls and ANSI CSI-ish escapes (ESC).
            if (value < 0x20 || value is 0x7F or 0x9B || value == 0x1B)
            {
                continue;
            }

            _ = sb.Append(rune.ToString());
        }

        // Strip leftover CSI sequences like "[32m".
        return AnsiSequenceRegex().Replace(sb.ToString(), string.Empty);
    }

    internal static IEnumerable<string> WrapToCellWidth(string text, int width)
    {
        int w = Math.Max(1, width);
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        // Soft-wrap by grapheme clusters using cell widths.
        List<string> graphemes = new();
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            graphemes.Add(enumerator.GetTextElement());
        }

        StringBuilder line = new();
        int col = 0;
        foreach (string g in graphemes)
        {
            int gw = ComposerLayout.MeasureGraphemeCellWidth(g, col);
            if (col > 0 && col + gw > w)
            {
                yield return line.ToString();
                line.Clear();
                col = 0;
                gw = ComposerLayout.MeasureGraphemeCellWidth(g, 0);
            }

            if (gw > w && line.Length == 0)
            {
                // Hard-split overwide grapheme by cells via TruncateCells path.
                string piece = TruncateCells(g, w);
                yield return piece;
                continue;
            }

            _ = line.Append(g);
            col += gw;
        }

        yield return line.ToString();
    }

    internal static string TruncateCells(string text, int maxCells)
    {
        if (maxCells <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder sb = new();
        int col = 0;
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            string g = enumerator.GetTextElement();
            int gw = ComposerLayout.MeasureGraphemeCellWidth(g, col);
            if (col + gw > maxCells)
            {
                break;
            }

            _ = sb.Append(g);
            col += gw;
        }

        return sb.ToString();
    }

    private static bool HasSensitiveOrContentBearingArgs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        if (!TryParseObject(json, out JsonElement el))
        {
            // Opaque non-JSON blob — treat as content-bearing.
            return LooksLikeHugeBlob(json) || json.Length > 240;
        }

        return ContainsSensitiveKeys(el);
    }

    private static bool ContainsSensitiveKeys(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (IsSensitiveKey(prop.Name))
            {
                return true;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object && ContainsSensitiveKeys(prop.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsObjectEmptyOrOnlySensitive(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (IsSummaryKey(prop.Name) && !IsSensitiveKey(prop.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static string? BuildSafeSummary(string? json)
    {
        if (!TryParseObject(json, out JsonElement el))
        {
            return null;
        }

        return BuildSafeSummaryFromElement(el);
    }

    private static string? BuildSafeSummaryFromElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        List<string> parts = new();
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            if (!IsSummaryKey(prop.Name) || IsSensitiveKey(prop.Name))
            {
                continue;
            }

            string? value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add($"{prop.Name}={Sanitize(value)}");
            if (parts.Count >= 3)
            {
                break;
            }
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static bool TryParseObject(string? json, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeHugeBlob(string text) =>
        text.Length > 400 || text.Count(static c => c is '\n' or '\r') > 3;

    [GeneratedRegex(
        @"content|body|payload|base64|patch|diff|replacement|old.?string|new.?string|api.?key|password|secret|token|authorization|bearer|client.?secret",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyRegex();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\[[0-9;]*m", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiSequenceRegex();
}
