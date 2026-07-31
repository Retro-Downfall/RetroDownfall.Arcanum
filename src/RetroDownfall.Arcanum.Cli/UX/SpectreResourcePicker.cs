using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

public sealed class SpectreResourcePicker(IThemePalette themePalette) : IResourcePicker
{
    public async Task<T?> PickAsync<T>(
        ResourcePickerRequest<T> request,
        CancellationToken cancellationToken)
        where T : class
    {
        string columns = string.Join(" | ", request.Descriptor.ColumnNames.Select(Markup.Escape));
        SelectionPrompt<T> prompt = new SelectionPrompt<T>()
            .Title(themePalette.HeadingBoldMarkup(Markup.Escape($"Select {request.Descriptor.SingularName}: {columns}")))
            .PageSize(15)
            .MoreChoicesText(themePalette.MutedMarkup(Markup.Escape("Move to see more; type to search; Esc cancels.")))
            .UseConverter(value => string.Join(
                " [grey]|[/] ",
                request.Descriptor.GetCells(value).Select(cell => Markup.Escape(cell ?? string.Empty))))
            .AddChoices(request.Choices)
            .AddCancelResult(() => null!);

        if (request.Searchable)
        {
            prompt.EnableSearch().SearchPlaceholderText("Type to filter");
        }

        return await prompt.ShowAsync(AnsiConsole.Console, cancellationToken).ConfigureAwait(false);
    }
}
