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

}
