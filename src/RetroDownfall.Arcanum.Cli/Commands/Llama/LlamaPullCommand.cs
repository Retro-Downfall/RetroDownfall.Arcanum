using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Llama;

public sealed class LlamaPullCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand<LlamaPullCommand.Settings>
{

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<URL>")]
        public string? Url { get; init; }

        [CommandOption("--cache-key")]
        public string? CacheKey { get; init; }

        [CommandOption("--sha256")]
        public string? Sha256 { get; init; }

    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("URL is required.")));

            return 1;
        }

        var request = new PullModelRequestDto
        {
            SourceUrl = settings.Url.Trim(),
            CacheKey = settings.CacheKey,
            Sha256 = settings.Sha256,
        };

        bool failed = false;

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
                        task.Value = task.MaxValue;

                        task.StopTask();

                        AnsiConsole.MarkupLine(
                            themePalette.HighlightMarkup(
                                Markup.Escape($"Cached model '{frame.CacheKey}' ready.")));
                    }
                }
            }).ConfigureAwait(false);

        return failed ? 1 : 0;

    }

}
