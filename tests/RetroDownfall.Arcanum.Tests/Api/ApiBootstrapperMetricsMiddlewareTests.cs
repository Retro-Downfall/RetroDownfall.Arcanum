using System.Collections.Concurrent;

using System.Diagnostics.Metrics;

using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

using RetroDownfall.Arcanum.Api;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Covers the <c>arcanum_http_requests_total</c> middleware installed by
/// <see cref="ApiBootstrapper.UseArcanumMetrics"/>. <c>ArcanumMetrics.Meter</c> is process-wide, so each
/// test routes through a unique route pattern and filters captured measurements down to that
/// <c>endpoint</c> label rather than asserting on the whole captured set.
/// </summary>
[Collection("Telemetry")]
public sealed class ApiBootstrapperMetricsMiddlewareTests
{

    [Fact]
    public async Task Metrics_middleware_counts_a_request_whose_pipeline_throws()
    {

        string route = "/" + NewProbeSegment() + "/throw";

        ConcurrentQueue<Dictionary<string, string>> captured = CaptureMeasurements(route, out MeterListener listener);

        using (listener)
        {

            await using WebApplication app = BuildProbeHost(route, static () => throw new InvalidOperationException("probe"));

            await app.StartAsync();

            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(route);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            await app.StopAsync();

        }

        Dictionary<string, string> tags = Assert.Single(captured);

        Assert.Equal(route, tags["endpoint"]);

        Assert.Equal("GET", tags["method"]);

        Assert.Equal("500", tags["status_code"]);

    }

    [Fact]
    public async Task Metrics_middleware_counts_a_request_that_completes_normally()
    {

        string route = "/" + NewProbeSegment() + "/ok";

        ConcurrentQueue<Dictionary<string, string>> captured = CaptureMeasurements(route, out MeterListener listener);

        using (listener)
        {

            await using WebApplication app = BuildProbeHost(route, static () => { });

            await app.StartAsync();

            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(route);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await app.StopAsync();

        }

        Dictionary<string, string> tags = Assert.Single(captured);

        Assert.Equal(route, tags["endpoint"]);

        Assert.Equal("GET", tags["method"]);

        Assert.Equal("200", tags["status_code"]);

    }

    private static string NewProbeSegment() => "metrics-probe-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Minimal host shaped like the production pipeline: a catch-all handler stands in for
    /// <c>UseArcanumExceptionHandler</c> and is registered <em>before</em> the metrics middleware, so a
    /// throwing endpoint unwinds through the metrics frame first exactly as it does under
    /// <c>arcanum serve</c>.
    /// </summary>
    private static WebApplication BuildProbeHost(string route, Action handler)
    {

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        WebApplication app = builder.Build();

        app.Use(static async (HttpContext context, Func<Task> next) =>
        {

            try
            {

                await next().ConfigureAwait(false);

            }
            catch (InvalidOperationException)
            {

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            }

        });

        app.UseArcanumMetrics();

        app.MapGet(route, handler);

        return app;

    }

    private static ConcurrentQueue<Dictionary<string, string>> CaptureMeasurements(string route, out MeterListener listener)
    {

        ConcurrentQueue<Dictionary<string, string>> captured = new();

        MeterListener created = new()
        {
            InstrumentPublished = static (instrument, activeListener) => activeListener.EnableMeasurementEvents(instrument),
        };

        created.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {

            if (instrument.Name != "arcanum_http_requests_total")
            {

                return;

            }

            Dictionary<string, string> snapshot = new(StringComparer.Ordinal);

            foreach (KeyValuePair<string, object?> tag in tags)
            {

                snapshot[tag.Key] = tag.Value?.ToString() ?? string.Empty;

            }

            if (snapshot.TryGetValue("endpoint", out string? endpoint) && endpoint == route)
            {

                captured.Enqueue(snapshot);

            }

        });

        created.Start();

        listener = created;

        return captured;

    }

}
