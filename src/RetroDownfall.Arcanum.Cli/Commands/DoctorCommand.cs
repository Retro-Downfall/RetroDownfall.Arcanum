using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class DoctorCommand(
    IOptions<ArcanumSettings> options,
    IHttpClientFactory httpClientFactory,
    ISecretStore secretStore,
    IThemePalette themePalette) : AsyncCommand
{

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        WriteVersionInfo();

        AnsiConsole.WriteLine();

        WritePathChecks();

        AnsiConsole.WriteLine();

        await WriteApiReachabilityAsync(cancellationToken).ConfigureAwait(false);

        return 0;
    }

    private void WriteVersionInfo()
    {

        AnsiConsole.Write(new Rule(themePalette.HeadingBoldMarkup(Markup.Escape("System")))
        {
            Justification = Justify.Left,
            Style = themePalette.HeadingStyle(),
        });

        AnsiConsole.WriteLine();

        string version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        AnsiConsole.MarkupLine(
            themePalette.MutedLabelMarkup(
                Markup.Escape("Arcanum:"),
                Markup.Escape(version)));

        AnsiConsole.MarkupLine(
            themePalette.MutedLabelMarkup(
                Markup.Escape("OS:"),
                Markup.Escape(RuntimeInformation.OSDescription)));

        AnsiConsole.MarkupLine(
            themePalette.MutedLabelMarkup(
                Markup.Escape("Runtime:"),
                Markup.Escape(RuntimeInformation.FrameworkDescription)));
    }

    private void WritePathChecks()
    {

        AnsiConsole.Write(new Rule(themePalette.HeadingBoldMarkup(Markup.Escape("Paths")))
        {
            Justification = Justify.Left,
            Style = themePalette.HeadingStyle(),
        });

        AnsiConsole.WriteLine();

        string grimoireDir = ArcanumPaths.GrimoireDirectory;

        WritePathStatus(
            "Grimoire directory",
            grimoireDir,
            Directory.Exists(grimoireDir),
            optional: false);

        string configFile = Path.Combine(grimoireDir, "arcanum.json");

        WritePathStatus(
            "arcanum.json",
            configFile,
            File.Exists(configFile),
            optional: true);

        string dbFile = ArcanumPaths.GrimoireDatabaseFile;

        WritePathStatus(
            "Grimoire database",
            dbFile,
            File.Exists(dbFile),
            optional: false);

        string securityFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "arcanum",
            "security.dat");

        WritePathStatus(
            "API key store (Data Protection)",
            securityFile,
            File.Exists(securityFile),
            optional: false);
    }

    private void WritePathStatus(string label, string path, bool exists, bool optional)
    {

        string escapedLabel = Markup.Escape(label + ":");

        string escapedPath = Markup.Escape(path);

        if (exists)
        {

            AnsiConsole.MarkupLine(
                themePalette.HighlightLabelMarkup(
                    escapedLabel,
                    escapedPath));
        }
        else if (optional)
        {

            AnsiConsole.MarkupLine(
                themePalette.MutedLabelMarkup(
                    escapedLabel,
                    Markup.Escape($"{path} (not found, optional)")));
        }
        else
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorLabelMarkup(
                    escapedLabel,
                    Markup.Escape($"{path} (not found)")));
        }
    }

    private async Task WriteApiReachabilityAsync(CancellationToken cancellationToken)
    {

        AnsiConsole.Write(new Rule(themePalette.HeadingBoldMarkup(Markup.Escape("API Health")))
        {
            Justification = Justify.Left,
            Style = themePalette.HeadingStyle(),
        });

        AnsiConsole.WriteLine();

        int port = ArcanumSettingClamps.HostPort(options.Value.Host.Port);

        string targetUrl = $"http://localhost:{port}/api/health";

        AnsiConsole.MarkupLine(
            themePalette.MutedLabelMarkup(
                Markup.Escape("Target:"),
                Markup.Escape(targetUrl)));

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        using HttpRequestMessage request = new(HttpMethod.Get, "api/health");

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        if (apiKey is not null)
        {

            _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);
        }

        int timeoutSeconds = ArcanumSettingClamps.DoctorHealthTimeoutSeconds(
            options.Value.Cli.DoctorHealthTimeoutSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {

                AnsiConsole.MarkupLine(
                    themePalette.HighlightMarkup(
                        Markup.Escape($"Reachable (HTTP {(int)response.StatusCode})")));
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {

                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(
                        Markup.Escape("API host is responding but authentication failed. Verify your API key or re-run 'arcanum serve'.")));
            }
            else
            {

                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(
                        Markup.Escape($"Unexpected response (HTTP {(int)response.StatusCode})")));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;
        }
        catch (OperationCanceledException)
        {

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape($"Timed out after {timeoutSeconds}s.")));
        }
        catch (HttpRequestException)
        {

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape("Not reachable. Is 'arcanum serve' running?")));
        }
    }

}
