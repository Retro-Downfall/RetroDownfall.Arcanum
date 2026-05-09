using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class ServeCommand(IThemePalette themePalette) : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
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
            AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape("New Master API Key generated and secured.")));
        }

        WebApplication app = builder.Build();

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
