using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;
using RetroDownfall.Arcanum.Core.Configuration;
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
builder.Configuration.AddArcanumConfiguration();
builder.WebHost.ConfigureKestrel(
    (WebHostBuilderContext ctx, KestrelServerOptions options) =>
    {
        string? raw = ctx.Configuration["Arcanum:Host:Port"];

        int configuredPort = new HostSettings().Port;

        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            configuredPort = parsed;
        }

        int port = ArcanumSettingClamps.HostPort(configuredPort);

        options.ListenLocalhost(port);
    });
builder.Logging.ClearProviders();
builder.Services.AddArcanumApiServices(builder.Configuration);
if (await ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync().ConfigureAwait(false) is { } newApiKey)
{
    Console.WriteLine("New Master API Key generated and secured.");
    Console.WriteLine(newApiKey);
}
WebApplication app = builder.Build();

app.UseArcanumCors();

app.UseArcanumRateLimiter();

app.MapArcanumEndpoints();
try
{
    await app.RunAsync().ConfigureAwait(false);
}
finally
{
    Log.CloseAndFlush();
}
