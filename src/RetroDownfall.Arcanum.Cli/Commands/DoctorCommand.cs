using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.Commands;

[ExcludeFromCodeCoverage] // Reason: interactive diagnostics command with environment-specific probes; not unit-testable without full host.
public sealed class DoctorCommand(
    IOptions<ArcanumSettings> options,
    IHttpClientFactory httpClientFactory,
    ISecretStore secretStore,
    IThemePalette themePalette,
    ICliEnvironment cliEnvironment) : AsyncCommand
{

    private const string OkGlyph = "\u2713";

    private const string WarnGlyph = "!";

    private const string FailGlyph = "\u2717";

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {

        WriteVersionPanel();

        AnsiConsole.WriteLine();

        bool healthy = WritePathsPanel();

        AnsiConsole.WriteLine();

        healthy &= WriteArcanumConfigPanel();

        AnsiConsole.WriteLine();

        healthy &= WriteMcpConfigPanel();

        AnsiConsole.WriteLine();

        healthy &= WriteTokenizerPanel();

        AnsiConsole.WriteLine();

        healthy &= await WriteApiReachabilityPanelAsync(cancellationToken).ConfigureAwait(false);

        return healthy ? 0 : 1;

    }

    private void WriteVersionPanel()
    {

        string version = RetroDownfall.Arcanum.Core.ArcanumBuildInfo.InformationalVersion;

        int plus = version.IndexOf('+');

        if (plus >= 0)
        {
            version = version[..plus];
        }

        Table table = BuildLabelTable(
            ("Arcanum", version),
            ("OS", RuntimeInformation.OSDescription),
            ("Runtime", RuntimeInformation.FrameworkDescription),
            ("Process arch", RuntimeInformation.ProcessArchitecture.ToString()),
            ("Interactive TTY", cliEnvironment.IsInteractive ? "yes" : "no (piped)"),
            ("Color enabled", cliEnvironment.ColorEnabled ? "yes" : "no"));

        WritePanel("System", table);

    }

    private bool WritePathsPanel()
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddColumn(new TableColumn(string.Empty));

        bool healthy = true;

        string grimoireDir = ArcanumPaths.GrimoireDirectory;

        healthy &= AddPathRow(table, "Grimoire directory", grimoireDir, Directory.Exists(grimoireDir), optional: false);

        string configFile = Path.Combine(grimoireDir, "arcanum.json");

        AddPathRow(table, "arcanum.json", configFile, File.Exists(configFile), optional: true);

        string dbFile = ArcanumPaths.GrimoireDatabaseFile;

        healthy &= AddPathRow(table, "Grimoire database", dbFile, File.Exists(dbFile), optional: false);

        string securityFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "arcanum",
            "security.dat");

        healthy &= AddPathRow(table, "API key store (Data Protection)", securityFile, File.Exists(securityFile), optional: false);

        WritePanel("Paths", table);

        return healthy;

    }

    private bool WriteArcanumConfigPanel()
    {

        string configFile = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddColumn(new TableColumn(string.Empty));

        if (!File.Exists(configFile))
        {

            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
                themePalette.MutedMarkup(Markup.Escape("arcanum.json:")),
                themePalette.MutedMarkup(Markup.Escape($"{configFile} (not found, optional)")));

            WritePanel("Configuration", table);

            return true;

        }

        bool healthy = true;

        try
        {

            ConfigurationBootstrapper.ValidateArcanumConfigurationFile(configFile);

            table.AddRow(
                themePalette.HighlightMarkup(Markup.Escape(OkGlyph)),
                themePalette.HighlightLabelMarkup(
                    Markup.Escape("arcanum.json:"),
                    Markup.Escape(configFile)),
                themePalette.MutedMarkup(Markup.Escape("valid JSON")));
        }
        catch (InvalidOperationException ex)
        {

            healthy = false;

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("arcanum.json:"),
                    Markup.Escape(configFile)),
                themePalette.ErrorMarkup(Markup.Escape(ex.Message)));
        }
        catch (IOException ex)
        {

            healthy = false;

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("arcanum.json:"),
                    Markup.Escape(configFile)),
                themePalette.ErrorMarkup(Markup.Escape("unreadable: " + ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {

            healthy = false;

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("arcanum.json:"),
                    Markup.Escape(configFile)),
                themePalette.ErrorMarkup(Markup.Escape("access denied: " + ex.Message)));
        }

        WritePanel("Configuration", table);

        return healthy;

    }

    private bool WriteMcpConfigPanel()
    {

        string globalMcpPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "arcanum",
            "mcp.json");

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddColumn(new TableColumn(string.Empty));

        if (!File.Exists(globalMcpPath))
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
                themePalette.MutedMarkup(Markup.Escape("Global mcp.json:")),
                themePalette.MutedMarkup(Markup.Escape($"{globalMcpPath} (not found, optional)")));

            WritePanel("MCP", table);

            return true;
        }

        bool healthy = true;

        try
        {
            byte[] raw = File.ReadAllBytes(globalMcpPath);

            using JsonDocument doc = JsonDocument.Parse(raw);

            int serverCount = 0;

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
                && servers.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty _ in servers.EnumerateObject())
                {
                    serverCount++;
                }
            }

            table.AddRow(
                themePalette.HighlightMarkup(Markup.Escape(OkGlyph)),
                themePalette.HighlightLabelMarkup(
                    Markup.Escape("Global mcp.json:"),
                    Markup.Escape(globalMcpPath)),
                themePalette.MutedMarkup(Markup.Escape($"{serverCount} server entry/entries parsed")));
        }
        catch (JsonException ex)
        {
            healthy = false;

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("Global mcp.json:"),
                    Markup.Escape(globalMcpPath)),
                themePalette.ErrorMarkup(Markup.Escape("invalid JSON: " + ex.Message)));
        }
        catch (IOException ex)
        {
            healthy = false;

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("Global mcp.json:"),
                    Markup.Escape(globalMcpPath)),
                themePalette.ErrorMarkup(Markup.Escape("unreadable: " + ex.Message)));
        }
        catch (UnauthorizedAccessException ex)
        {
            healthy = false;

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("Global mcp.json:"),
                    Markup.Escape(globalMcpPath)),
                themePalette.ErrorMarkup(Markup.Escape("access denied: " + ex.Message)));
        }

        WritePanel("MCP", table);

        return healthy;

    }

    private bool WriteTokenizerPanel()
    {

        string encoding = string.IsNullOrWhiteSpace(options.Value.Intelligence.TokenizerEncoding)
            ? "o200k_base"
            : options.Value.Intelligence.TokenizerEncoding.Trim();

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddColumn(new TableColumn(string.Empty));

        bool healthy = true;

        try
        {
            Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding(encoding);

            int tokenCount = tokenizer.CountTokens("arcanum doctor smoke test");

            table.AddRow(
                themePalette.HighlightMarkup(Markup.Escape(OkGlyph)),
                themePalette.HighlightLabelMarkup(
                    Markup.Escape("Tokenizer:"),
                    Markup.Escape(encoding)),
                themePalette.MutedMarkup(Markup.Escape($"smoke test counted {tokenCount} tokens")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            healthy = false;

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorLabelMarkup(
                    Markup.Escape("Tokenizer:"),
                    Markup.Escape(encoding)),
                themePalette.ErrorMarkup(
                    Markup.Escape(
                        $"failed ({ex.GetType().Name}: {ex.Message}). Confirm Microsoft.ML.Tokenizers.Data.O200kBase is referenced and the encoding name is valid.")));
        }

        WritePanel("Tokenizer", table);

        return healthy;

    }

    private async Task<bool> WriteApiReachabilityPanelAsync(CancellationToken cancellationToken)
    {

        int port = ArcanumSettingClamps.HostPort(options.Value.Host.Port);

        int timeoutSeconds = ArcanumSettingClamps.DoctorHealthTimeoutSeconds(
            options.Value.Cli.DoctorHealthTimeoutSeconds);

        string targetUrl = $"http://localhost:{port}/api/health";

        HttpClient client = httpClientFactory.CreateClient("ArcanumApi");

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        DoctorProbeResult probe = await ProbeApiReachabilityAsync(
                client,
                apiKey,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddColumn(new TableColumn(string.Empty));

        bool healthy = true;

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape(" ")),
            themePalette.MutedMarkup(Markup.Escape("Target:")),
            themePalette.MutedMarkup(Markup.Escape(targetUrl)));

        switch (probe.Kind)
        {
            case DoctorProbeKind.Ok:
                table.AddRow(
                    themePalette.HighlightMarkup(Markup.Escape(OkGlyph)),
                    themePalette.HighlightLabelMarkup(
                        Markup.Escape("Status:"),
                        Markup.Escape($"Reachable (HTTP {probe.HttpStatus})")),
                    themePalette.MutedMarkup(Markup.Escape("API key accepted.")));
                break;

            case DoctorProbeKind.Unauthorized:
                healthy = false;

                table.AddRow(
                    themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                    themePalette.ErrorLabelMarkup(
                        Markup.Escape("Status:"),
                        Markup.Escape($"Reached host (HTTP {probe.HttpStatus}) but auth failed")),
                    themePalette.ErrorMarkup(Markup.Escape("Verify your API key or re-run 'arcanum serve'.")));
                break;

            case DoctorProbeKind.UnexpectedStatus:
                healthy = false;

                table.AddRow(
                    themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                    themePalette.ErrorLabelMarkup(
                        Markup.Escape("Status:"),
                        Markup.Escape($"Unexpected HTTP {probe.HttpStatus}")),
                    themePalette.MutedMarkup(Markup.Escape(probe.Detail ?? string.Empty)));
                break;

            case DoctorProbeKind.Timeout:
                table.AddRow(
                    themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
                    themePalette.MutedMarkup(Markup.Escape("Status:")),
                    themePalette.MutedMarkup(
                        Markup.Escape(
                            $"Timed out after {timeoutSeconds}s. Raise Arcanum:Cli:DoctorHealthTimeoutSeconds if startup is slow.")));
                break;

            case DoctorProbeKind.Unreachable:
                table.AddRow(
                    themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
                    themePalette.MutedMarkup(Markup.Escape("Status:")),
                    themePalette.MutedMarkup(Markup.Escape("Not reachable. Is 'arcanum serve' running?")));
                break;

            case DoctorProbeKind.Cancelled:
                table.AddRow(
                    themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
                    themePalette.MutedMarkup(Markup.Escape("Status:")),
                    themePalette.MutedMarkup(Markup.Escape("Cancelled by operator.")));
                break;
        }

        WritePanel("API Health", table);

        return healthy;

    }

    private async Task<DoctorProbeResult> ProbeApiReachabilityAsync(
        HttpClient client,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {

        Task<DoctorProbeResult> RunProbeAsync()
        {
            return ProbeApiReachabilityInnerAsync(client, apiKey, timeoutSeconds, cancellationToken);
        }

        if (!cliEnvironment.IsInteractive || !cliEnvironment.ColorEnabled)
        {
            return await RunProbeAsync().ConfigureAwait(false);
        }

        DoctorProbeResult result = default;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(themePalette.HighlightStyle())
            .StartAsync(
                $"Probing /api/health (timeout {timeoutSeconds}s)...",
                async _ =>
                {
                    result = await RunProbeAsync().ConfigureAwait(false);
                })
            .ConfigureAwait(false);

        return result;

    }

    private static async Task<DoctorProbeResult> ProbeApiReachabilityInnerAsync(
        HttpClient client,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {

        using HttpRequestMessage request = new(HttpMethod.Get, "api/health");

        if (apiKey is not null)
        {
            _ = request.Headers.TryAddWithoutValidation(ArcanumApiHeaders.ApiKey, apiKey);
        }

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            int statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return new DoctorProbeResult(DoctorProbeKind.Ok, statusCode, null);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return new DoctorProbeResult(DoctorProbeKind.Unauthorized, statusCode, null);
            }

            return new DoctorProbeResult(DoctorProbeKind.UnexpectedStatus, statusCode, response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DoctorProbeResult(DoctorProbeKind.Cancelled, 0, null);
        }
        catch (OperationCanceledException)
        {
            return new DoctorProbeResult(DoctorProbeKind.Timeout, 0, null);
        }
        catch (HttpRequestException ex)
        {
            return new DoctorProbeResult(DoctorProbeKind.Unreachable, 0, ex.Message);
        }

    }

    private bool AddPathRow(Table table, string label, string path, bool exists, bool optional)
    {

        string escapedLabel = Markup.Escape(label + ":");

        string escapedPath = Markup.Escape(path);

        if (exists)
        {
            table.AddRow(
                themePalette.HighlightMarkup(Markup.Escape(OkGlyph)),
                themePalette.HighlightMarkup(escapedLabel),
                themePalette.TextMarkup(escapedPath));

            return true;
        }

        if (optional)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
                themePalette.MutedMarkup(escapedLabel),
                themePalette.MutedMarkup(Markup.Escape($"{path} (not found, optional)")));

            return true;
        }

        table.AddRow(
            themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
            themePalette.ErrorMarkup(escapedLabel),
            themePalette.ErrorMarkup(Markup.Escape($"{path} (not found)")));

        return false;

    }

    private Table BuildLabelTable(params (string Label, string Value)[] rows)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        foreach ((string label, string value) in rows)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape(label + ":")),
                themePalette.TextMarkup(Markup.Escape(value)));
        }

        return table;

    }

    private void WritePanel(string title, IRenderable content)
    {

        Panel panel = new(content)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape(title))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HeadingStyle(),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
        };

        AnsiConsole.Write(panel);

    }

    private enum DoctorProbeKind
    {

        Ok,

        Unauthorized,

        UnexpectedStatus,

        Timeout,

        Unreachable,

        Cancelled,
    }

    private readonly record struct DoctorProbeResult(DoctorProbeKind Kind, int HttpStatus, string? Detail);

}
