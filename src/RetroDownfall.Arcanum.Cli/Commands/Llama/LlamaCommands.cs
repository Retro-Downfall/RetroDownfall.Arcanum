using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Llama;

/// <summary>
/// Manage local llama-server instances and GGUF model cache (requires arcanum serve).
/// </summary>
public sealed class LlamaCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// Download a GGUF model into the local cache.
    /// </summary>
    /// <param name="url">Absolute http or https URL of the GGUF file to download.</param>
    /// <param name="cacheKey">Optional cache directory name; defaults to a hash of the source URL.</param>
    /// <param name="sha256">Expected SHA-256 hex digest of the downloaded file (verified after download).</param>
    [Command("pull")]
    public async Task<int> Pull(
        [Argument] string url,
        string? cacheKey = null,
        string? sha256 = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(url))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("URL is required.")));

            return 1;
        }

        var request = new PullModelRequestDto
        {
            SourceUrl = url.Trim(),
            CacheKey = cacheKey,
            Sha256 = sha256,
        };

        bool failed = false;

        bool completed = false;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .StartAsync(async ctx =>
            {
                ProgressTask task = ctx.AddTask(themePalette.HighlightMarkup(Markup.Escape("Downloading model")));

                task.IsIndeterminate = true;

                await foreach (LlamaPullProgress frame in apiClient.PullModelStreamAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (!string.IsNullOrEmpty(frame.Error))
                    {
                        task.StopTask();

                        AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(frame.Error)));

                        failed = true;

                        return;
                    }

                    if (frame.TotalBytes is > 0)
                    {
                        task.IsIndeterminate = false;

                        task.MaxValue = frame.TotalBytes.Value;

                        task.Value = frame.BytesDownloaded;
                    }

                    if (frame.Completed)
                    {
                        completed = true;

                        task.Value = task.MaxValue;

                        task.StopTask();

                        AnsiConsole.MarkupLine(
                            themePalette.HighlightMarkup(
                                Markup.Escape($"Cached model '{frame.CacheKey}' ready.")));
                    }
                }
            }).ConfigureAwait(false);

        if (failed)
        {
            return 1;
        }

        if (!completed)
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("Model download stream ended without a completion frame.")));

            return 1;

        }

        return 0;

    }

    /// <summary>
    /// Start llama-server for a cached model.
    /// </summary>
    /// <param name="cacheKey">Cache key of the GGUF model to load (from llama pull or llama status).</param>
    /// <param name="gpuLayers">Number of model layers to offload to GPU (llama-server -ngl).</param>
    /// <param name="port">Local TCP port for the spawned llama-server instance.</param>
    [Command("start")]
    public async Task<int> Start(
        [Argument] string cacheKey,
        int? gpuLayers = null,
        int? port = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Cache key is required.")));

            return 1;
        }

        // W4.1: validate the optional --port / --gpu-layers flags client-side.
        if (port is int p && (p < 1 || p > 65535))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--port must be between 1 and 65535.")));

            return 1;
        }

        if (gpuLayers is int layers && layers < -1)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--gpu-layers must be -1 (auto/all) or a non-negative integer.")));

            return 1;
        }

        Result<LlamaServerInfo> result = await apiClient
            .StartLlamaServerAsync(cacheKey, gpuLayers, port, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        LlamaServerInfo server = result.Value;

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(
                Markup.Escape($"llama-server healthy at {server.Endpoint} (state: {server.State}, port: {server.Port}).")));

        return 0;

    }

    /// <summary>
    /// Stop one or all llama-server instances.
    /// </summary>
    /// <param name="cacheKey">Cache key of a running llama-server; omit to stop all instances.</param>
    [Command("stop")]
    public async Task<int> Stop([Argument] string? cacheKey = null, CancellationToken cancellationToken = default)
    {

        Result<bool> result = await apiClient.StopLlamaServerAsync(cacheKey, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        string message = string.IsNullOrWhiteSpace(cacheKey)
            ? "Stopped all llama-server instances."
            : $"Stopped llama-server for '{cacheKey}'.";

        AnsiConsole.MarkupLine(themePalette.HighlightMarkup(Markup.Escape(message)));

        return 0;

    }

    /// <summary>
    /// List running servers and cached models.
    /// </summary>
    [Command("status")]
    public async Task<int> Status(CancellationToken cancellationToken)
    {

        Result<LlamaServerInfo[]> serversResult = await apiClient.ListLlamaServersAsync(cancellationToken).ConfigureAwait(false);

        if (serversResult.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(serversResult.Error));

            return 1;
        }

        Result<CachedModelInfo[]> modelsResult = await apiClient.ListCachedModelsAsync(cancellationToken).ConfigureAwait(false);

        if (modelsResult.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(modelsResult.Error));

            return 1;
        }

        Table serversTable = new();

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Cache key")));

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("State")));

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Endpoint")));

        serversTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("PID")));

        foreach (LlamaServerInfo server in serversResult.Value)
        {
            serversTable.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(server.CacheKey))),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.State.ToString()))),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.Endpoint))),
                new Markup(themePalette.TextMarkup(Markup.Escape(server.ProcessId?.ToString() ?? "-"))));
        }

        AnsiConsole.Write(new Panel(serversTable)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Running servers"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        });

        if (serversResult.Value.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No llama-server instances are running.")));
        }

        Table modelsTable = new();

        modelsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Cache key")));

        modelsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Size")));

        modelsTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Last accessed")));

        foreach (CachedModelInfo model in modelsResult.Value)
        {
            string sizeText = FormatBytes(model.Size);

            modelsTable.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(model.CacheKey))),
                new Markup(themePalette.TextMarkup(Markup.Escape(sizeText))),
                new Markup(themePalette.TextMarkup(Markup.Escape(model.LastAccessedAt.ToString("u")))));
        }

        AnsiConsole.Write(new Panel(modelsTable)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Cached models"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        });

        if (modelsResult.Value.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No GGUF models are cached.")));
        }

        return 0;

    }

    internal static string FormatBytes(long bytes)
    {

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KiB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):F1} MiB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):F1} GiB";

    }

}
