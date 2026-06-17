using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Wards;

public sealed record WardDto(
    string WardId,
    string ToolName,
    JsonElement? Arguments,
    string? SessionId,
    DateTimeOffset PlacedAt,
    DateTimeOffset ExpiresAt);

public sealed record ResolveWardRequest(bool Allow, string? Reason);

public sealed record WardResolutionDto(
    string WardId,
    bool Allowed,
    string? Reason,
    DateTimeOffset ResolvedAt);
