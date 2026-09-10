using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Exact column contracts and readers for the small relational core of the Grimoire.
/// </summary>
/// <remarks>
/// Every projection names its columns in schema order and is materialized without reflection.
/// Changing a table therefore changes one compile-visible mapping instead of an inferred runtime
/// model. Repository-specific projections should remain beside their repository.
/// </remarks>
internal static class GrimoireEntitySql
{
    public const string CampaignColumns =
        "\"Id\", \"Name\", \"NameLower\", \"Path\", \"Type\", \"Description\", "
        + "\"Settings\", \"SanctumConfigJson\", \"CreatedAt\", \"UpdatedAt\"";

    public const string SessionColumns =
        "\"Id\", \"CampaignId\", \"Title\", \"Status\", \"CreatedAt\", \"UpdatedAt\", "
        + "\"Summary\", \"LastSummarizedMessageAt\", \"TotalTokensUsed\", \"TotalCostUsd\", "
        + "\"UnsummarizedEntryCount\", \"ForkedFromSessionId\"";

    public const string EntryColumns =
        "\"Id\", \"SessionId\", \"Role\", \"Content\", \"ModelUsed\", \"CreatedAt\", "
        + "\"Sequence\", \"ToolCallId\", \"ToolName\", \"ToolArguments\", \"IsPinned\"";

    public const string PromptColumns =
        "\"Id\", \"CampaignId\", \"Name\", \"Version\", \"Description\", \"Tags\", "
        + "\"Template\", \"ParameterSchema\", \"DefaultParameters\", \"Model\", \"Provider\", "
        + "\"Temperature\", \"TopP\", \"MaxOutputTokens\", \"CreatedAt\", \"UpdatedAt\"";

    public const string ApprenticeColumns =
        "\"Id\", \"CampaignId\", \"Name\", \"Goal\", \"Plan\", \"CurrentStep\", "
        + "\"Status\", \"SessionId\", \"WorkspacePath\", \"CheckpointData\", \"ErrorMessage\", "
        + "\"CreatedAt\", \"UpdatedAt\"";

    public static Campaign ReadCampaign(SqliteDataReader reader) =>
        new()
        {
            Id = ReadGuid(reader, 0),
            Name = reader.GetString(1),
            NameLower = reader.GetString(2),
            Path = reader.GetString(3),
            Type = (WorkspaceType)reader.GetInt32(4),
            Description = ReadNullableString(reader, 5),
            Settings = reader.GetString(6),
            SanctumConfigJson = reader.GetString(7),
            CreatedAt = ReadDateTimeOffset(reader, 8),
            UpdatedAt = ReadDateTimeOffset(reader, 9),
        };

    public static Session ReadSession(SqliteDataReader reader) =>
        new()
        {
            Id = ReadGuid(reader, 0),
            CampaignId = ReadNullableGuid(reader, 1),
            Title = ReadNullableString(reader, 2),
            Status = reader.GetString(3),
            CreatedAt = ReadDateTimeOffset(reader, 4),
            UpdatedAt = ReadDateTimeOffset(reader, 5),
            Summary = ReadNullableString(reader, 6),
            LastSummarizedMessageAt = ReadNullableDateTime(reader, 7),
            TotalTokensUsed = reader.GetInt64(8),
            TotalCostUsd = ExactUsdText.Read(reader, 9),
            UnsummarizedEntryCount = reader.GetInt32(10),
            ForkedFromSessionId = ReadNullableGuid(reader, 11),
        };

    public static Entry ReadEntry(SqliteDataReader reader) =>
        new()
        {
            Id = ReadGuid(reader, 0),
            SessionId = ReadGuid(reader, 1),
            Role = (MessageRole)reader.GetInt32(2),
            Content = reader.GetString(3),
            ModelUsed = reader.GetString(4),
            CreatedAt = ReadDateTimeOffset(reader, 5),
            Sequence = reader.GetInt64(6),
            ToolCallId = ReadNullableString(reader, 7),
            ToolName = ReadNullableString(reader, 8),
            ToolArguments = ReadNullableString(reader, 9),
            IsPinned = reader.GetBoolean(10),
        };

    public static Prompt ReadPrompt(SqliteDataReader reader) =>
        new()
        {
            Id = ReadGuid(reader, 0),
            CampaignId = ReadNullableGuid(reader, 1),
            Name = reader.GetString(2),
            Version = reader.GetString(3),
            Description = ReadNullableString(reader, 4),
            Tags = reader.GetString(5),
            Template = reader.GetString(6),
            ParameterSchema = ReadNullableString(reader, 7),
            DefaultParameters = ReadNullableString(reader, 8),
            Model = ReadNullableString(reader, 9),
            Provider = ReadNullableString(reader, 10),
            Temperature = ReadNullableDouble(reader, 11),
            TopP = ReadNullableDouble(reader, 12),
            MaxOutputTokens = ReadNullableInt32(reader, 13),
            CreatedAt = ReadDateTimeOffset(reader, 14),
            UpdatedAt = ReadDateTimeOffset(reader, 15),
        };

    public static Apprentice ReadApprentice(SqliteDataReader reader) =>
        new()
        {
            Id = ReadGuid(reader, 0),
            CampaignId = ReadNullableGuid(reader, 1),
            Name = reader.GetString(2),
            Goal = reader.GetString(3),
            Plan = reader.GetString(4),
            CurrentStep = reader.GetInt32(5),
            Status = reader.GetString(6),
            SessionId = ReadNullableGuid(reader, 7),
            WorkspacePath = reader.GetString(8),
            CheckpointData = ReadNullableString(reader, 9),
            ErrorMessage = ReadNullableString(reader, 10),
            CreatedAt = ReadDateTimeOffset(reader, 11),
            UpdatedAt = ReadDateTimeOffset(reader, 12),
        };

    public static SqliteParameter AddParameter(
        SqliteCommand command,
        string name,
        object? value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public static string Format(DateTimeOffset value) =>
        UtcInstantText.Format(value);

    public static string Format(DateTime value) =>
        UtcInstantText.Format(value);

    public static string Format(Guid value) =>
        value.ToString("D").ToUpperInvariant();

    public static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal) =>
        UtcInstantText.Parse(reader.GetString(ordinal));

    public static DateTime? ReadNullableDateTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : UtcInstantText.ParseDateTime(reader.GetString(ordinal));

    public static Guid ReadGuid(SqliteDataReader reader, int ordinal) =>
        Guid.Parse(reader.GetString(ordinal));

    public static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    public static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static double? ReadNullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    public static int? ReadNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
