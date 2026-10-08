using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>One selectable model, with the provider it belongs to.</summary>
internal sealed record ModelPickerItem(string Model, string ProviderName);

/// <summary>
/// The pure part of the Command Center model drop-down: what to show, how typing narrows it, and
/// which model a selected row means.
/// </summary>
/// <remarks>
/// <para>
/// The drop-down exists because <c>/model &lt;name&gt;</c> requires the operator to already know the
/// model id before they can type it. That was tolerable when every model was one an operator had
/// written into <c>arcanum.json</c> themselves; with a Familiar the available set belongs to the
/// vendor and changes without a configuration edit, so selection has to be discoverable.
/// </para>
/// <para>
/// The list comes from <c>GET /api/models</c>, which is where the hide list is already applied — so
/// a hidden model is absent here for the same reason it is absent everywhere else, and
/// <c>/model &lt;name&gt;</c> still accepts it because that path checks the name against
/// <c>GET /api/providers</c>, which reports the hide list beside the offered models (see
/// <see cref="CommandCenterModelChoice"/>).
/// </para>
/// </remarks>
internal static class CommandCenterModelPicker
{
    /// <summary>Marks the model prompts currently go to.</summary>
    internal const string ActiveMarker = "●";

    /// <summary>
    /// Groups models by provider in configured order, de-duplicating a model id that the same
    /// provider advertises twice. The same id on two providers is kept twice on purpose: they are
    /// genuinely different routes, and collapsing them would hide which one a selection picks.
    /// </summary>
    public static IReadOnlyList<ModelPickerItem> Build(IReadOnlyList<ModelInfoDto>? models)
    {
        List<ModelPickerItem> items = [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, ModelInfoDto> group in (models ?? [])
            .Where(static model => !string.IsNullOrWhiteSpace(model.Model))
            .GroupBy(static model => model.ProviderName, StringComparer.Ordinal))
        {
            foreach (ModelInfoDto model in group)
            {
                if (seen.Add($"{group.Key}\0{model.Model}"))
                {
                    items.Add(new ModelPickerItem(model.Model, group.Key));
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Type-ahead narrowing over both the model id and the provider name, so an operator can find a
    /// model either by what it is called or by where it comes from.
    /// </summary>
    public static IReadOnlyList<ModelPickerItem> Filter(
        IReadOnlyList<ModelPickerItem> items,
        string? filter)
    {
        ArgumentNullException.ThrowIfNull(items);

        string needle = (filter ?? string.Empty).Trim();

        if (needle.Length == 0)
        {
            return items;
        }

        return
        [
            .. items.Where(item =>
                item.Model.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || item.ProviderName.Contains(needle, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>Renders one row per model, marking the active one.</summary>
    public static IReadOnlyList<string> Render(
        IReadOnlyList<ModelPickerItem> items,
        string? activeModel)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            // An empty list is a state, not an error: with no model declared for a Familiar there is
            // nothing to offer, and `/model <name>` still works.
            return ["No models to choose from — use /model <name>."];
        }

        List<string> lines = new(items.Count);

        foreach (ModelPickerItem item in items)
        {
            string marker = string.Equals(item.Model, activeModel, StringComparison.OrdinalIgnoreCase)
                ? ActiveMarker
                : " ";

            // A model id and a provider name come from the provider's /models answer, and this list
            // fills the overlay without passing through ShowOverlay, so it is stripped here.
            lines.Add(TerminalTextSanitizer.SanitizeLine($"{marker} {item.Model}  ({item.ProviderName})"));
        }

        return lines;
    }

    /// <summary>The model a selected row means, or null when the row is the empty-state line.</summary>
    public static string? Resolve(IReadOnlyList<ModelPickerItem> items, int selectedIndex) =>
        ResolveItem(items, selectedIndex)?.Model;

    /// <summary>
    /// The model and provider a selected row means, or null when the row is the empty-state line. The
    /// provider is what lets the drop-down confirm a selection in the same words <c>/model</c> does.
    /// </summary>
    public static ModelPickerItem? ResolveItem(IReadOnlyList<ModelPickerItem> items, int selectedIndex)
    {
        ArgumentNullException.ThrowIfNull(items);

        return selectedIndex >= 0 && selectedIndex < items.Count
            ? items[selectedIndex]
            : null;
    }

    /// <summary>
    /// The header control's label. Kept short: it shares the header row with the status line and
    /// must never force the layout to grow.
    /// </summary>
    public static string RenderSelector(string? activeModel, bool focused)
    {
        string model = string.IsNullOrWhiteSpace(activeModel) ? "(default)" : TerminalTextSanitizer.SanitizeLine(activeModel);

        return focused ? $"[ model: {model} ▾ ]" : $"model: {model} ▾";
    }
}
