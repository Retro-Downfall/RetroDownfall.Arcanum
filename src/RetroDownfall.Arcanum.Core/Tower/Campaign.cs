using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.Tower;

public sealed class Campaign
{

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NameLower { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public WorkspaceType Type { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// The campaign's serialized <see cref="CampaignSettings"/>. Defaults to an empty JSON object, the
    /// way <see cref="SanctumConfigJson"/> does, rather than an empty string: a row nobody ever wrote
    /// settings for still has to deserialize through the record's own defaults, because an absence must
    /// never derive an un-warded campaign.
    /// </summary>
    public string Settings { get; set; } = "{}";

    public string SanctumConfigJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

}
