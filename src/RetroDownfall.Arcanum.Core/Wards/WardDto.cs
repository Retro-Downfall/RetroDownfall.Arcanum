using System.Text.Json;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Wards;

public sealed record WardDto(
    string WardId,
    string ToolName,
    JsonElement? Arguments,
    string? SessionId,
    DateTimeOffset PlacedAt,
    DateTimeOffset ExpiresAt);

public sealed record ResolveWardRequest(bool Allow, string? Reason);

/// <summary>
/// Result of <c>POST /api/wards/{id}</c>. <see cref="Origin"/> is additive and is always
/// <see cref="WardResolutionOrigin.Human"/> here — this endpoint exists only for operator
/// resolutions; automatic outcomes never become live wards and are observed on the
/// <c>warded</c> / <c>wardResolved</c> frames instead.
/// </summary>
public sealed record WardResolutionDto(
    string WardId,
    bool Allowed,
    string? Reason,
    DateTimeOffset ResolvedAt,
    WardResolutionOrigin Origin = WardResolutionOrigin.Human);
