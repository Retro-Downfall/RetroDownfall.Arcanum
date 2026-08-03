namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Session-attachment runtime projection. Activation comes from
/// <c>Arcanum:Features:Attachments</c>; storage and prompt envelopes are code-owned.
/// </summary>
public sealed record AttachmentsSettings
{

    public bool Enabled { get; set; } = true;

    public long MaxBytesPerSession { get; set; } = 256L * 1024L * 1024L;

    public int PendingRetentionHours { get; set; } = 24;

    public int MaxIndexItemsInPrompt { get; set; } = 40;

    public int MaxIndexBytesInPrompt { get; set; } = 4_096;

    public bool EnableModelAttachTool { get; set; } = true;

}
