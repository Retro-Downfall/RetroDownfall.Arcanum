using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using Serilog;

if (SandboxExecHelper.TryHandle(args, typeof(Program)))
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

WebApplication app = builder.Build();

app.UseArcanumExceptionHandler();

app.UseArcanumResponseCompression();

app.UseArcanumCors();

app.UseArcanumRateLimiter();

app.UseArcanumMetrics();

app.MapArcanumEndpoints();

try
{
    await app.StartAsync().ConfigureAwait(false);

    string? newApiKey = app.Services
        .GetRequiredService<GrimoireDatabaseHostedService>()
        .TakeGeneratedMasterApiKey();

    if (newApiKey is not null)
    {
        Console.WriteLine(newApiKey);

        Log.Information("New Master API Key generated and secured. Save this key — it will not be shown again.");
    }

    Console.Error.WriteLine($"Arcanum DevHost listening on http://localhost:{listenPort}");

    await app.WaitForShutdownAsync().ConfigureAwait(false);

    return 0;
}
finally
{
    try
    {
        await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Arcanum DevHost StopAsync during cleanup failed.");
    }

    await app.DisposeAsync().ConfigureAwait(false);

    Log.CloseAndFlush();
}
