namespace RetroDownfall.Arcanum.Api.Models;

public sealed record InstanceMetadataDto(
    string Version,
    string OsDescription,
    string RuntimeIdentifier,
    int ProcessId,
    DateTimeOffset StartTime,
    TimeSpan Uptime,
    bool NativeAot,
    string GrimoireDirectory,
    string ConfigPath,
    int Port,
    bool ListenAny,
    bool LoreSystemEnabled,
    bool ArchiveSearchEnabled,
    bool ContextCompressionEnabled,
    bool TokenTrackingEnabled,
    bool HttpsEnabled,
    int HttpsPort,
    string? HttpsUrl,
    string? HttpUrl);
