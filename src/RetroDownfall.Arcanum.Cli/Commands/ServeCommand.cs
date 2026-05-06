using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ServeCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.Host.UseWindowsService(options => options.ServiceName = "ArcanumDaemon");

        builder.Host.UseSystemd();

        builder.Configuration.AddArcanumConfiguration();

        builder.WebHost.ConfigureKestrel(
            (WebHostBuilderContext ctx, KestrelServerOptions options) =>
            {
                int configuredPort = ReadConfiguredHostPort(ctx.Configuration);

                int port = ArcanumSettingClamps.HostPort(configuredPort);

                if (ShouldBindArcanumHostAny())
                {
                    options.ListenAnyIP(port);
                }
                else
                {
                    options.ListenLocalhost(port);
                }
            });

        builder.Logging.ClearProviders();

        builder.Services.AddArcanumApiServices(builder.Configuration);

        if (await ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            AnsiConsole.MarkupLine("[green]New Master API Key generated and secured.[/]");
        }

        WebApplication app = builder.Build();

        app.MapArcanumEndpoints();

        CancellationTokenRegistration stopRegistration = cancellationToken.Register(
            static state => ((IHostApplicationLifetime)state!).StopApplication(),
            app.Lifetime);

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            await stopRegistration.DisposeAsync().ConfigureAwait(false);

            Log.CloseAndFlush();
        }

        return 0;
    }

    private static int ReadConfiguredHostPort(IConfiguration configuration)
    {
        string? raw = configuration["Arcanum:Host:Port"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HostSettings().Port;
        }

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : new HostSettings().Port;
    }

    private static bool ShouldBindArcanumHostAny()
    {
        string? raw = Environment.GetEnvironmentVariable("ARCANUM_HOST_ANY");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();

        if (string.Equals(trimmed, "1", StringComparison.Ordinal))
        {
            return true;
        }

        return bool.TryParse(trimmed, out bool parsed) && parsed;
    }
}
