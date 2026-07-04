using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Telemetry;

namespace RetroDownfall.Arcanum.Api.Telemetry;

/// <summary>
/// Maps <c>GET /metrics</c> — Prometheus text format (<c>0.0.4</c>). Mapped onto <c>app</c> directly
/// (standalone, unauthenticated) or onto the <c>/api</c> group (behind <c>ApiKeyEndpointFilter</c> and
/// any active rate limiter) by <c>ApiBootstrapper.MapArcanumEndpoints</c>, depending on the effective
/// <c>Arcanum:Metrics:RequireApiKey</c> gate.
/// </summary>
internal static class MetricsEndpoints
{

    public static void MapMetricsEndpoint(this IEndpointRouteBuilder endpoints)
    {

        endpoints.MapGet("/metrics", async (
            PrometheusMetricsExporter exporter,
            ArcanumDbContext db,
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {

            if (!settings.Value.Metrics.Enabled)
            {

                httpContext.Response.StatusCode = StatusCodes.Status404NotFound;

                return;

            }

            // A single indexed-column count, cheap enough to run on every scrape (typical Prometheus
            // scrape interval is 15s) — no caching, so the exporter singleton stays a pure renderer with
            // no database dependency of its own.
            long activeSessions = await db.Sessions
                .AsNoTracking()
                .CountAsync(s => s.Status == "active", cancellationToken)
                .ConfigureAwait(false);

            string body = await exporter.RenderMetricsAsync(activeSessions, cancellationToken).ConfigureAwait(false);

            httpContext.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";

            await httpContext.Response.WriteAsync(body, cancellationToken).ConfigureAwait(false);

        })
        .WithName("GetMetrics");

    }

}
