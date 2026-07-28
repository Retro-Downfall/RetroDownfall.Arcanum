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
/// Maps <c>GET /metrics</c> — Prometheus text format (<c>0.0.4</c>). Always registered at the
/// canonical <c>/metrics</c> path (not under <c>/api</c>). <c>ApiBootstrapper.MapArcanumEndpoints</c>
/// attaches <c>ApiKeyEndpointFilter</c> (and the active rate limiter when enabled) when the effective
/// <c>Arcanum:Security:MetricsRequireApiKey</c> gate is on.
/// </summary>
internal static class MetricsEndpoints
{

    public static RouteHandlerBuilder MapMetricsEndpoint(this IEndpointRouteBuilder endpoints)
    {

        return endpoints.MapGet("/metrics", async (
            PrometheusMetricsExporter exporter,
            ArcanumDbContext db,
            IOptionsSnapshot<ArcanumSettings> settings,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {

            if (!settings.Value.ResolveMetrics().Enabled)
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
