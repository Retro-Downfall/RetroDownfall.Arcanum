namespace RetroDownfall.Arcanum.Api.Models;

public sealed record InstanceMetadataDto(
    string Version,
    string OsDescription,
    string RuntimeIdentifier,
    int ProcessId,
    DateTimeOffset StartTime,
    string GrimoireDirectory,
    string ConfigPath,
    int Port,
    bool ListenAny,
    bool LoreSystemEnabled,
    bool ArchiveSearchEnabled,
    bool ContextCompressionEnabled,
    bool TokenTrackingEnabled,
    bool LlamaCppEnabled);
