using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Hosting;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Infrastructure.Security;

using Serilog;

using RetroDownfall.Arcanum.Core.Configuration;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(5001));

builder.Logging.ClearProviders();

builder.Configuration.AddArcanumConfiguration();

builder.Services.AddArcanumApiServices(builder.Configuration);

if (await ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync().ConfigureAwait(false) is { } newApiKey)
{

    Console.WriteLine("New Master API Key generated and secured.");

    Console.WriteLine(newApiKey);

}

WebApplication app = builder.Build();

app.MapArcanumEndpoints();

try
{

    await app.RunAsync().ConfigureAwait(false);

}

finally
{

    Log.CloseAndFlush();

}
