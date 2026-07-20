namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Session attachment persistence. Bound from <c>Arcanum:Attachments</c>.</summary>
public sealed record AttachmentsSettings
{

    public bool Enabled { get; set; } = true;

    public int MaxReferencesPerTurn { get; set; } = 8;

    public int MaxVersionsPerLogicalKey { get; set; } = 20;

    public long MaxBytesPerSession { get; set; } = 256L * 1024L * 1024L;

    public int PendingRetentionHours { get; set; } = 24;

    public int MaxIndexItemsInPrompt { get; set; } = 40;

    public int MaxIndexBytesInPrompt { get; set; } = 4_096;

    public bool EnableModelAttachTool { get; set; } = true;

}
