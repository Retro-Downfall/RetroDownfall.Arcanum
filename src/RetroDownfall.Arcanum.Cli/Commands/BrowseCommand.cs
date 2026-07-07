using System.Text.Json;
using ConsoleAppFramework;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Browse a web URL using the built-in browse_web tool.
/// </summary>
public sealed class BrowseCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    private const int ContentPreviewLength = 500;

    [Command("browse")]
    public async Task<int> Run([Argument] string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(new Error(ErrorCodes.Validation.InvalidBody, "URL is required.")));

            return 1;
        }

        using JsonDocument arguments = BuildArgumentsDocument(url.Trim());

        JsonElement argumentsElement = arguments.RootElement.Clone();

        Result<ToolInvokeResponse> result = await apiClient
            .InvokeToolAsync("browse_web", argumentsElement, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        BrowseWebResult? browseResult;

        try
        {
            browseResult = result.Value.Result.Deserialize(ArcanumJsonContext.Default.BrowseWebResult);
        }
        catch (JsonException)
        {
            browseResult = null;
        }

        if (browseResult is null)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(new Error(ErrorCodes.Validation.InvalidBody, "Invalid response from browse_web tool.")));

            return 1;
        }

        Panel titlePanel = new(new Markup(Markup.Escape(browseResult.Title)))
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup("Page Title")),
        };

        AnsiConsole.Write(titlePanel);

        string content = browseResult.Content;

        string preview = content.Length <= ContentPreviewLength
            ? content
            : string.Concat(content.AsSpan(0, ContentPreviewLength), "...");

        Panel contentPanel = new(new Markup(Markup.Escape(preview)))
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup("Content Preview")),
        };

        AnsiConsole.Write(contentPanel);

        if (browseResult.Links.Count > 0)
        {
            Table table = new();

            table.AddColumn("Link");

            foreach (string link in browseResult.Links)
            {
                table.AddRow(Markup.Escape(link));
            }

            AnsiConsole.Write(table);
        }

        return 0;
    }

    private static JsonDocument BuildArgumentsDocument(string url)
    {
        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();

            writer.WriteString("url", url);

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray());
    }

}
