using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

TaskScheduler.UnobservedTaskException += static (_, e) =>
{

    Log.Error(e.Exception, "Unobserved task exception.");

    e.SetObserved();

};

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

if (!string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
{

    if (await ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync().ConfigureAwait(false) is string newApiKey)
    {

        Console.WriteLine(newApiKey);

        Log.Information("New Master API Key generated and secured. Save this key — it will not be shown again.");

    }

}

WebApplication app = builder.Build();

app.UseArcanumExceptionHandler();

app.UseArcanumCors();

app.UseArcanumRateLimiter();

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
