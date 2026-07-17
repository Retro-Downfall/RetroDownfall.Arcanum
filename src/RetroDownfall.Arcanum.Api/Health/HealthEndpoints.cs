using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Health;

internal static class HealthEndpoints
{

    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet("/health", async (ArcanumHealthChecker healthChecker, HttpContext httpContext, CancellationToken cancellationToken) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            HealthReportDto report = await healthChecker
                .BuildReportAsync(cancellationToken)
                .ConfigureAwait(false);

            Result<HealthReportDto> healthResult = Result<HealthReportDto>.Success(report);

            ApiResponse<HealthReportDto> response = ApiResponse<HealthReportDto>.FromResult(healthResult, traceId);

            int statusCode = report.Status == HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;

            return Results.Json(response, ArcanumJsonContext.Default.ApiResponseHealthReportDto, statusCode: statusCode);

        })
        .WithName("GetHealth");

        apiGroup.MapGet("/grimoire/stats", async (GrimoireStatsService statsService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            GrimoireStatsDto stats = await statsService.GetStatsAsync(cancellationToken).ConfigureAwait(false);

            Result<GrimoireStatsDto> statsResult = Result<GrimoireStatsDto>.Success(stats);

            ApiResponse<GrimoireStatsDto> response = ApiResponse<GrimoireStatsDto>.FromResult(statsResult, traceId);

            return Results.Ok(response);

        })
        .WithName("GetGrimoireStats");

        apiGroup.MapGet("/meta", (IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            DateTimeOffset startTime;
            TimeSpan uptime;

            try
            {

                using Process process = Process.GetCurrentProcess();

                startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                uptime = DateTimeOffset.UtcNow - startTime;

            }
            catch (InvalidOperationException) // process has already exited
            {

                startTime = DateTimeOffset.UtcNow;
                uptime = TimeSpan.Zero;

            }
            catch (PlatformNotSupportedException)
            {

                startTime = DateTimeOffset.UtcNow;
                uptime = TimeSpan.Zero;

            }

            bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(settings.Value.Host.ListenAny);

            // ListenAny refuses to start without HTTPS, so any running any-IP host is TLS-bound.
            bool httpsEnabled = settings.Value.Host.Https.Enabled || listenAny;

            int httpsPort = ArcanumSettingClamps.HostHttpsPort(settings.Value.Host.Https.Port);

            string? httpsUrl = ArcanumLocalApiAddress.ResolveHttpsUrl(settings.Value.Host, httpsEnabled);

            string? httpUrl = ArcanumLocalApiAddress.ResolveHttpUrl(settings.Value.Host, listenAny);

            InstanceMetadataDto metadata = new(
                Version: GetInformationalVersion(),
                OsDescription: RuntimeInformation.OSDescription,
                RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
                ProcessId: Environment.ProcessId,
                StartTime: startTime,
                Uptime: uptime,
                NativeAot: !RuntimeFeature.IsDynamicCodeSupported,
                GrimoireDirectory: ArcanumPaths.GrimoireDirectory,
                ConfigPath: Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"),
                Port: ArcanumSettingClamps.HostPort(settings.Value.Host.Port),
                ListenAny: listenAny,
                LoreSystemEnabled: settings.Value.Intelligence.EnableLoreSystem,
                ArchiveSearchEnabled: settings.Value.Intelligence.EnableArchiveSearch,
                ContextCompressionEnabled: settings.Value.Intelligence.EnableContextCompression,
                TokenTrackingEnabled: settings.Value.Intelligence.EnableTokenTracking,
                HttpsEnabled: httpsEnabled,
                HttpsPort: httpsPort,
                HttpsUrl: httpsUrl,
                HttpUrl: httpUrl);

            Result<InstanceMetadataDto> metadataResult = metadata;

            ApiResponse<InstanceMetadataDto> response = ApiResponse<InstanceMetadataDto>.FromResult(metadataResult, traceId);

            return Results.Ok(response);
        })
        .WithName("GetInstanceMetadata");

        return apiGroup;
    }

    private static string GetInformationalVersion()
    {

        string version = RetroDownfall.Arcanum.Core.ArcanumBuildInfo.InformationalVersion;

        int plus = version.IndexOf('+');

        if (plus >= 0)
        {

            version = version[..plus];

        }

        return version;

    }

}
