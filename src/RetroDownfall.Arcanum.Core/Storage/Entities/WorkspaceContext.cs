namespace RetroDownfall.Arcanum.Core.Storage.Entities;

public sealed class WorkspaceContext
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string WorkspacePath { get; set; } = string.Empty;

    public string SerializedSnapshot { get; set; } = string.Empty;
}
