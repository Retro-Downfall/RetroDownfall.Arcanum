namespace RetroDownfall.Arcanum.Api.Models;

public enum HealthStatus
{

    Healthy,

    Degraded,

    Unhealthy,

}

public sealed record HealthComponentDto(
    string Name,
    HealthStatus Status,
    string? Detail);

public sealed record HealthReportDto(
    HealthStatus Status,
    HealthComponentDto[] Components);
