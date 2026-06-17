using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

builder.Configuration.AddArcanumConfiguration();

string? portRaw = builder.Configuration["Arcanum:Host:Port"];

int configuredPort = new HostSettings().Port;

if (!string.IsNullOrWhiteSpace(portRaw)
    && int.TryParse(portRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort))
{
    configuredPort = parsedPort;
}

int listenPort = ArcanumSettingClamps.HostPort(configuredPort);

builder.WebHost.ConfigureKestrel(
    (WebHostBuilderContext ctx, KestrelServerOptions options) =>
    {
        options.ListenLocalhost(listenPort);

        long maxBodyBytes = ArcanumSettingClamps.MaxRequestBodyBytes(
            ReadConfiguredMaxRequestBodyBytes(ctx.Configuration));

        options.Limits.MaxRequestBodySize = maxBodyBytes;
    });

builder.Logging.ClearProviders();

builder.Services.AddArcanumApiServices(builder.Configuration);

if (await ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync().ConfigureAwait(false) is not null)
{
    Log.Information("New Master API Key generated and secured. Retrieve it from the Grimoire security store.");
}

WebApplication app = builder.Build();

app.UseArcanumCors();

app.UseArcanumRateLimiter();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);

        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            context.Response.ContentType = "application/json";

            string traceId = Activity.Current?.Id ?? context.TraceIdentifier;

            ApiResponse<string> body = new(null, false, new Error("Internal", "An internal error occurred."), traceId);

            await context.Response.WriteAsJsonAsync(
                body,
                ArcanumJsonContext.Default.ApiResponseString,
                cancellationToken: CancellationToken.None);
        }
    }
});

app.MapArcanumEndpoints();

Console.WriteLine($"Arcanum DevHost listening on http://localhost:{listenPort}");

try
{
    await app.RunAsync().ConfigureAwait(false);
}
finally
{
    Log.CloseAndFlush();
}

static long ReadConfiguredMaxRequestBodyBytes(IConfiguration configuration)
{
    string? raw = configuration["Arcanum:Host:MaxRequestBodyBytes"];

    if (string.IsNullOrWhiteSpace(raw))
    {
        return new HostSettings().MaxRequestBodyBytes;
    }

    return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
        ? parsed
        : new HostSettings().MaxRequestBodyBytes;
}
