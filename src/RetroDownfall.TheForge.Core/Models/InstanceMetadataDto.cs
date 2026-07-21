namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.InstanceMetadataDto</c> (wire shape of
/// <c>GET /api/meta</c>). Kept in TheForge.Core to avoid referencing the Api project.
/// </summary>
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
    string? HttpUrl,
    bool EmbeddingsEnabled,
    string EmbeddingsVectorMode,
    string EmbeddingsVectorDiagnostic,
    int EmbeddingsManagedSearchRowBudget,
    string Edition,
    bool HostProcessToolsAllowed);
