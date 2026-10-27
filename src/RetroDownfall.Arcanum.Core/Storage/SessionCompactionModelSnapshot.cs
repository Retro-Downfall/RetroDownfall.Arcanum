namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>Nonwire metadata for the newest persisted assistant answer, excluding tool-call representations.</summary>
public sealed record SessionCompactionModelSnapshot(string ModelUsed, bool HasContent, bool HasRecordedModel);
