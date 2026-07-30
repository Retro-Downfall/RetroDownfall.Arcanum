using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

if (SandboxExecHelper.TryHandle(args))
{
    return 0;
}

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

if (!string.Equals(builder.Environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(builder.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Arcanum DevHost is intended for Development or Testing environments. Current environment: {builder.Environment.EnvironmentName}.");
}

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
        ArcanumKestrelConfigurator.Configure(options, ctx.Configuration, listenAny: false);
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

app.UseArcanumResponseCompression();

app.UseArcanumCors();

app.UseArcanumRateLimiter();

app.UseArcanumMetrics();

app.MapArcanumEndpoints();

Console.Error.WriteLine($"Arcanum DevHost listening on http://localhost:{listenPort}");

try
{
    await app.RunAsync().ConfigureAwait(false);

    return 0;
}
finally
{
    Log.CloseAndFlush();
}
