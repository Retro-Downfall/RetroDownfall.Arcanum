using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed class Campaign
{

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NameLower { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public WorkspaceType Type { get; set; }

    public string? Description { get; set; }

    public string Settings { get; set; } = string.Empty;

    public string SanctumConfigJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

}
