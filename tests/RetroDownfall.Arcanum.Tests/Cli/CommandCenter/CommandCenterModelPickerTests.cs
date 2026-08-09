using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// <c>/model &lt;name&gt;</c> assumes the operator already knows the model id. That was fine when
/// every model was one they had written into <c>arcanum.json</c>; with a Familiar the set belongs to
/// the vendor and changes without a configuration edit, so selection has to be discoverable and
/// keyboard-operable. These facts pin the drop-down's behaviour: what it lists, how typing narrows
/// it, and that it agrees with <c>/model</c>.
/// </summary>
public sealed class CommandCenterModelPickerTests
{

    private static readonly ModelInfoDto[] Models =
    [
        new("gpt-4o", "compat", "OpenAICompatible", "***", 128_000),
        new("gpt-4o-mini", "compat", "OpenAICompatible", "***", 128_000),
        new("claude-sonnet", "ClaudeCode-subscription", "ClaudeCodeCli", string.Empty, 200_000),
    ];

    [Fact]
    public void Every_provider_kind_appears_in_one_list()
    {

        IReadOnlyList<ModelPickerItem> items = CommandCenterModelPicker.Build(Models);

        Assert.Equal(
            ["gpt-4o", "gpt-4o-mini", "claude-sonnet"],
            items.Select(static item => item.Model));

    }

    [Fact]
    public void Models_are_grouped_by_provider_in_configured_order()
    {

        ModelInfoDto[] interleaved =
        [
            new("gpt-4o", "compat", "OpenAICompatible", "***", 128_000),
            new("claude-sonnet", "ClaudeCode-subscription", "ClaudeCodeCli", string.Empty, 200_000),
            new("gpt-4o-mini", "compat", "OpenAICompatible", "***", 128_000),
        ];

        IReadOnlyList<ModelPickerItem> items = CommandCenterModelPicker.Build(interleaved);

        Assert.Equal(
            ["gpt-4o", "gpt-4o-mini", "claude-sonnet"],
            items.Select(static item => item.Model));

    }

    /// <summary>
    /// The same id on two providers is genuinely two routes; collapsing them would hide which one a
    /// selection picks.
    /// </summary>
    [Fact]
    public void The_same_model_on_two_providers_is_offered_twice()
    {

        ModelInfoDto[] duplicated =
        [
            new("shared", "one", "OpenAICompatible", "***", 8_192),
            new("shared", "two", "OpenAICompatible", "***", 8_192),
        ];

        IReadOnlyList<ModelPickerItem> items = CommandCenterModelPicker.Build(duplicated);

        Assert.Equal(["one", "two"], items.Select(static item => item.ProviderName));

    }

    [Fact]
    public void The_active_model_is_marked()
    {

        IReadOnlyList<string> lines = CommandCenterModelPicker.Render(
            CommandCenterModelPicker.Build(Models),
            activeModel: "claude-sonnet");

        Assert.Contains(
            lines,
            static line =>
                line.StartsWith(CommandCenterModelPicker.ActiveMarker, StringComparison.Ordinal)
                && line.Contains("claude-sonnet", StringComparison.Ordinal));

        Assert.DoesNotContain(
            lines,
            static line =>
                line.StartsWith(CommandCenterModelPicker.ActiveMarker, StringComparison.Ordinal)
                && line.Contains("gpt-4o-mini", StringComparison.Ordinal));

    }

    [Fact]
    public void Every_row_names_the_provider_the_model_comes_from()
    {

        IReadOnlyList<string> lines = CommandCenterModelPicker.Render(
            CommandCenterModelPicker.Build(Models),
            activeModel: null);

        Assert.All(lines, static line => Assert.Contains('(', line));

    }

    [Theory]
    [InlineData("mini", new[] { "gpt-4o-mini" })]
    [InlineData("MINI", new[] { "gpt-4o-mini" })]
    [InlineData("claude", new[] { "claude-sonnet" })]
    [InlineData("", new[] { "gpt-4o", "gpt-4o-mini", "claude-sonnet" })]
    public void Type_ahead_narrows_by_model_name(string filter, string[] expected)
    {

        IReadOnlyList<ModelPickerItem> filtered = CommandCenterModelPicker.Filter(
            CommandCenterModelPicker.Build(Models),
            filter);

        Assert.Equal(expected, filtered.Select(static item => item.Model));

    }

    /// <summary>
    /// An operator who knows where a model comes from but not what it is called can narrow by
    /// provider — which is the ordinary case for a subscription-backed CLI.
    /// </summary>
    [Fact]
    public void Type_ahead_also_narrows_by_provider_name()
    {

        IReadOnlyList<ModelPickerItem> filtered = CommandCenterModelPicker.Filter(
            CommandCenterModelPicker.Build(Models),
            "ClaudeCode");

        Assert.Equal(["claude-sonnet"], filtered.Select(static item => item.Model));

    }

    [Fact]
    public void A_selected_row_resolves_to_its_model_id()
    {

        IReadOnlyList<ModelPickerItem> items = CommandCenterModelPicker.Build(Models);

        Assert.Equal("gpt-4o-mini", CommandCenterModelPicker.Resolve(items, 1));

    }

    [Fact]
    public void An_out_of_range_selection_resolves_to_nothing_rather_than_a_wrong_model()
    {

        IReadOnlyList<ModelPickerItem> items = CommandCenterModelPicker.Build(Models);

        Assert.Null(CommandCenterModelPicker.Resolve(items, 99));

        Assert.Null(CommandCenterModelPicker.Resolve(items, -1));

    }

    /// <summary>
    /// With a Familiar declaring no models the list can legitimately be empty, and the drop-down
    /// still has to say something useful — the free-text path is not a failure state.
    /// </summary>
    [Fact]
    public void An_empty_list_renders_a_row_pointing_at_the_slash_command()
    {

        IReadOnlyList<string> lines = CommandCenterModelPicker.Render([], activeModel: null);

        string only = Assert.Single(lines);

        Assert.Contains("/model", only, StringComparison.Ordinal);

    }

    [Fact]
    public void An_empty_list_resolves_to_nothing()
    {

        Assert.Null(CommandCenterModelPicker.Resolve([], 0));

    }

    [Theory]
    [InlineData(null, "(default)")]
    [InlineData("", "(default)")]
    [InlineData("claude-sonnet", "claude-sonnet")]
    public void The_header_control_names_the_current_model(string? model, string expected)
    {

        Assert.Contains(
            expected,
            CommandCenterModelPicker.RenderSelector(model, focused: false),
            StringComparison.Ordinal);

    }

    [Fact]
    public void The_header_control_shows_that_it_has_focus()
    {

        Assert.NotEqual(
            CommandCenterModelPicker.RenderSelector("claude-sonnet", focused: false),
            CommandCenterModelPicker.RenderSelector("claude-sonnet", focused: true));

    }

}

/// <summary>
/// Keyboard routing for the drop-down. It has to be operable end to end without a mouse, and it must
/// not disturb the Sessions picker it shares an overlay with.
/// </summary>
public sealed class CommandCenterModelPickerKeymapTests
{

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    internal void Enter_space_or_down_opens_the_drop_down(bool enter, bool space, bool down)
    {

        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Model,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: false,
            new KeyChord(IsEnter: enter, IsSpace: space, IsDown: down));

        Assert.Equal(CommandCenterAction.OpenModelPicker, action);

    }

    /// <summary>
    /// Everything else in the region is inert on purpose: a stray key must never silently change
    /// which model prompts go to.
    /// </summary>
    [Fact]
    internal void An_unrelated_key_on_the_selector_does_nothing()
    {

        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Model,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: false,
            new KeyChord(IsBareLetter: true));

        Assert.Equal(CommandCenterAction.None, action);

    }

    [Theory]
    [InlineData(true, false, CommandCenterAction.ModelSelectUp)]
    [InlineData(false, true, CommandCenterAction.ModelSelectDown)]
    internal void Arrows_move_the_model_selection_not_the_session_selection(
        bool up,
        bool down,
        CommandCenterAction expected)
    {

        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Overlay,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: true,
            new KeyChord(IsUp: up, IsDown: down),
            CommandCenterOverlayKind.ModelPicker);

        Assert.Equal(expected, action);

    }

    [Fact]
    internal void The_sessions_picker_keeps_its_own_selection_movement()
    {

        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Overlay,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: true,
            new KeyChord(IsDown: true),
            CommandCenterOverlayKind.SessionPicker);

        Assert.Equal(CommandCenterAction.SessionSelectDown, action);

    }

    [Fact]
    internal void Enter_in_the_model_overlay_selects_the_model()
    {

        Assert.Equal(
            CommandCenterAction.SelectModel,
            CommandCenterKeymap.MapOverlayEnter(CommandCenterOverlayKind.ModelPicker));

    }

    /// <summary>Esc cancels back to the composer, the same way it leaves every other overlay.</summary>
    [Fact]
    internal void Esc_cancels_the_drop_down_back_to_the_composer()
    {

        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Overlay,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: true,
            new KeyChord(IsEsc: true),
            CommandCenterOverlayKind.ModelPicker);

        Assert.Equal(CommandCenterAction.CloseOverlayOrFocusComposer, action);

    }

    /// <summary>Tab must not cycle panes out from under a modal overlay.</summary>
    [Fact]
    internal void Tab_is_inert_while_the_drop_down_is_open()
    {

        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Overlay,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: true,
            new KeyChord(IsTab: true),
            CommandCenterOverlayKind.ModelPicker);

        Assert.Equal(CommandCenterAction.NoOp, action);

    }

}
