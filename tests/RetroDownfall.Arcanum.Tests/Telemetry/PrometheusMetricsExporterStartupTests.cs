using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.Telemetry;

namespace RetroDownfall.Arcanum.Tests.Telemetry;

/// <summary>
/// A <c>MeterListener</c> only observes measurements recorded after it starts, and the exporter is a lazy singleton that nothing but
/// <c>GET /metrics</c> resolves, so a host that left it unresolved lost every measurement recorded before the first scrape (the sweeps parked
/// and readmitted by the Long Rest worker among them). Host composition resolves it, so it listens from the start.
/// </summary>
[Collection("Telemetry")]
public sealed class PrometheusMetricsExporterStartupTests
{
    [Fact]
    public async Task The_exporter_listens_from_host_composition_and_renders_a_measurement_recorded_before_the_first_scrape()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddSingleton<PrometheusMetricsExporter>();

        await using WebApplication app = builder.Build();

        app.UseArcanumMetrics();

        string probe = "probe-" + Guid.NewGuid().ToString("N");

        // Recorded after composition and before anything has asked for the exporter: the first scrape is far later in a real host.
        ArcanumMetrics.LongRestSweepParkedTotal.Add(3, new KeyValuePair<string, object?>("class", probe));

        string body = await app.Services.GetRequiredService<PrometheusMetricsExporter>().RenderMetricsAsync();

        string line = Assert.Single(body.Split('\n'), candidate => candidate.Contains(probe, StringComparison.Ordinal));

        Assert.StartsWith("arcanum_long_rest_sweep_parked", line, StringComparison.Ordinal);

        Assert.EndsWith(" 3", line.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_without_the_exporter_still_composes()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        await using WebApplication app = builder.Build();

        app.UseArcanumMetrics();

        Assert.Null(app.Services.GetService<PrometheusMetricsExporter>());
    }
}
