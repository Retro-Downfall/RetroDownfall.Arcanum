namespace RetroDownfall.Arcanum.Core.DataLifecycle;

public static class DataRetentionDataClassParser
{
    public static bool TryParse(
        string? value,
        out RetentionDataClass dataClass)
    {
        dataClass = default;

        string normalized = (value ?? string.Empty)
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        normalized = normalized.ToLowerInvariant() switch
        {
            "attachments" => nameof(RetentionDataClass.AttachmentVersions),

            "workspaceindexes" => nameof(RetentionDataClass.WorkspaceChunks),

            "accounting" => nameof(RetentionDataClass.InferenceRuns),

            "daemonhistory" => nameof(RetentionDataClass.DaemonExecutions),

            _ => normalized,
        };

        return normalized.Length > 0
            && normalized.All(char.IsLetter)
            && Enum.TryParse(
                normalized,
                ignoreCase: true,
                out dataClass)
            && Enum.IsDefined(dataClass);
    }

    /// <summary>Canonical command spelling of a closed retention class.</summary>
    public static string Format(RetentionDataClass dataClass)
    {
        string name = dataClass.ToString();

        System.Text.StringBuilder result = new(name.Length + 4);

        for (int index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index])) result.Append('-');

            result.Append(char.ToLowerInvariant(name[index]));
        }

        return result.ToString();
    }

    /// <summary>A bounded single-line rendering of rejected operator input, never a logging value.</summary>
    public static string DescribeInput(string? value)
    {
        System.Text.StringBuilder result = new();

        foreach (char character in value ?? string.Empty)
        {
            string part = char.IsControl(character) || char.IsSurrogate(character)
                || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
                ? "\\u" + ((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)
                : character is '\\' or '\'' ? "\\" + character : character.ToString();

            if (result.Length + part.Length > 128)
            {
                result.Append("...");

                break;
            }

            result.Append(part);
        }

        return result.ToString();
    }
}
