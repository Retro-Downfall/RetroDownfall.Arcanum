using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

public sealed class SpectreResourcePicker(IThemePalette themePalette) : IResourcePicker
{
    public async Task<ResourcePickerResult<T>> PickAsync<T>(
        ResourcePickerRequest<T> request,
        CancellationToken cancellationToken)
        where T : class
    {
        string columns = string.Join(" | ", request.Descriptor.ColumnNames.Select(Markup.Escape));

        int nextPageIndex = request.Choices.Count;

        int choiceCount = request.Choices.Count + (request.HasNextPage ? 1 : 0);

        SelectionPrompt<int> prompt = new SelectionPrompt<int>()
            .Title(themePalette.HeadingBoldMarkup(Markup.Escape($"Select {request.Descriptor.SingularName}: {columns}")))
            .PageSize(15)
            .MoreChoicesText(themePalette.MutedMarkup(Markup.Escape("Move to see more; type to search; Esc cancels.")))
            .UseConverter(index => index == nextPageIndex && request.HasNextPage
                ? themePalette.MutedMarkup("Load next page…")
                : string.Join(
                    " [grey]|[/] ",
                    request.Descriptor.GetCells(request.Choices[index])
                        .Select(cell => Markup.Escape(cell ?? string.Empty))))
            .AddChoices(Enumerable.Range(0, choiceCount))
            .AddCancelResult(() => -1);

        if (request.Searchable)
        {
            prompt.EnableSearch().SearchPlaceholderText("Type to filter");
        }

        int selected = await prompt.ShowAsync(AnsiConsole.Console, cancellationToken).ConfigureAwait(false);

        if (selected < 0)
        {
            return ResourcePickerResult<T>.Cancelled();
        }

        if (request.HasNextPage && selected == nextPageIndex)
        {
            return ResourcePickerResult<T>.NextPage();
        }

        return ResourcePickerResult<T>.Selected(request.Choices[selected]);
    }
}
