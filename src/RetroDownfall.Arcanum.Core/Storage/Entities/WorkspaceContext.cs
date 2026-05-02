namespace RetroDownfall.Arcanum.Core.Storage.Entities;

public sealed class WorkspaceContext
{

    public Guid Id { get; set; }

    public string RootPath { get; set; } = string.Empty;

    public string ProjectSummary { get; set; } = string.Empty;

    public DateTime LastScanned { get; set; }

}
