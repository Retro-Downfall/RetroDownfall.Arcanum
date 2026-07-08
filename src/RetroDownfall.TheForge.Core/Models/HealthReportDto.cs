namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.HealthStatus</c>. Kept in TheForge.Core
/// so the client avoids referencing the ASP.NET-heavy Api project (see docs/THE_FORGE.md). Serializes
/// as an integer (0/1/2) — the source type carries no <c>JsonStringEnumConverter</c>, so this mirror
/// must not add one either.
/// </summary>
public enum HealthStatus
{

    Healthy,

    Degraded,

    Unhealthy,

}

/// <summary>Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.HealthComponentDto</c>.</summary>
public sealed record HealthComponentDto(
    string Name,
    HealthStatus Status,
    string? Detail);

/// <summary>Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.HealthReportDto</c> (wire shape of <c>GET /api/health</c>).</summary>
public sealed record HealthReportDto(
    HealthStatus Status,
    HealthComponentDto[] Components);
