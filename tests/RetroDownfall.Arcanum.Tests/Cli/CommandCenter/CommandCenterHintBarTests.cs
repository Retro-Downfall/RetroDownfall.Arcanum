using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The footer is one row. Cutting the joined hints off at the terminal edge left half a hint on screen
/// ("… Ctrl+C cancel · C…") and dropped F1, the one key that lists every other key. The hint bar keeps
/// whole hints in priority order and, whenever it leaves any out, says where they are.
/// </summary>
public sealed class CommandCenterHintBarTests
{
    /// <summary>What the composer footer shows on a 120-column terminal (118 cells inside the frame).</summary>
    internal const string ComposerFooterAt120Columns =
        "/ commands · Ctrl+K palette · Ctrl+N new session · Shift+Tab model · Ctrl+O sessions · Ctrl+C cancel · F1 help";

    /// <summary>What the composer footer shows on an 80-column terminal (78 cells inside the frame).</summary>
    internal const string ComposerFooterAt80Columns =
        "/ commands · Ctrl+K palette · Ctrl+N new session · Shift+Tab model · F1 help";

    [Fact]
    public void At_120_columns_the_composer_footer_keeps_six_hints_and_ends_with_F1()
    {
        Assert.Equal(
            ComposerFooterAt120Columns,
            CommandCenterHintBar.Fit(CommandCenterGuidance.ComposerHints(modelSelectorVisible: true), 118));
    }

    [Fact]
    public void At_80_columns_the_composer_footer_keeps_four_hints_and_ends_with_F1()
    {
        Assert.Equal(
            ComposerFooterAt80Columns,
            CommandCenterHintBar.Fit(CommandCenterGuidance.ComposerHints(modelSelectorVisible: true), 78));
    }

    /// <summary>The model control is not rendered on a narrow terminal, so the footer must not point at it.</summary>
    [Fact]
    public void The_composer_footer_offers_the_model_control_only_when_it_is_visible()
    {
        Assert.Contains("Shift+Tab model", CommandCenterGuidance.ComposerHints(modelSelectorVisible: true));

        Assert.DoesNotContain("Shift+Tab model", CommandCenterGuidance.ComposerHints(modelSelectorVisible: false));
    }

    /// <summary>The composer footer leads with the ways to find everything else.</summary>
    [Fact]
    public void The_composer_footer_leads_with_commands_palette_new_session_and_model()
    {
        string[] lead = ["/ commands", "Ctrl+K palette", "Ctrl+N new session", "Shift+Tab model"];

        IReadOnlyList<string> hints = CommandCenterGuidance.ComposerHints(modelSelectorVisible: true);

        Assert.Equal(lead, hints.Take(lead.Length));

        Assert.Equal(CommandCenterHintBar.HelpHint, hints[^1]);
    }

    [Fact]
    public void A_list_that_fits_comes_back_whole_and_unchanged()
    {
        IReadOnlyList<string> hints = CommandCenterGuidance.ComposerHints(modelSelectorVisible: true);

        string full = string.Join(CommandCenterHintBar.Separator, hints);

        Assert.Equal(full, CommandCenterHintBar.Fit(hints, TerminalCellMetrics.MeasureWidth(full)));
    }

    [Fact]
    public void Fit_never_overflows_never_splits_a_hint_and_ends_with_F1_whenever_it_drops_one()
    {
        int smallest = TerminalCellMetrics.MeasureWidth(CommandCenterHintBar.HelpHint);

        foreach (IReadOnlyList<string> hints in AllHintLists())
        {
            string full = string.Join(CommandCenterHintBar.Separator, hints);

            for (int width = smallest; width <= 140; width++)
            {
                string fitted = CommandCenterHintBar.Fit(hints, width);

                Assert.True(
                    TerminalCellMetrics.MeasureWidth(fitted) <= width,
                    $"`{fitted}` overflows {width} cells.");

                foreach (string segment in fitted.Split(CommandCenterHintBar.Separator))
                {
                    Assert.True(
                        hints.Contains(segment) || segment == CommandCenterHintBar.HelpHint,
                        $"`{segment}` in `{fitted}` is not a whole hint.");
                }

                if (TerminalCellMetrics.MeasureWidth(full) <= width)
                {
                    Assert.Equal(full, fitted);
                }
                else
                {
                    Assert.EndsWith(CommandCenterHintBar.HelpHint, fitted, StringComparison.Ordinal);
                }
            }
        }
    }

    /// <summary>The last resort on a frame narrower than F1 help itself: never wider than the frame.</summary>
    [Fact]
    public void A_frame_narrower_than_F1_help_gets_it_truncated()
    {
        Assert.Equal(
            "F1 h",
            CommandCenterHintBar.Fit(CommandCenterGuidance.ComposerHints(modelSelectorVisible: true), 4));
    }

    /// <summary>
    /// Ctrl+Enter arrives as the same CR as Enter in Terminal.app, iTerm2's defaults, tmux and xterm.js,
    /// so no hint may teach it.
    /// </summary>
    [Fact]
    public void No_hint_list_teaches_Ctrl_Enter()
    {
        foreach (IReadOnlyList<string> hints in AllHintLists())
        {
            Assert.DoesNotContain(hints, static hint => hint.Contains("Ctrl+Enter", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_ask_human_hints_are_the_ask_human_footer()
    {
        Assert.Equal(
            CommandCenterGuidance.HumanPromptFooter,
            string.Join(CommandCenterHintBar.Separator, CommandCenterGuidance.HumanPromptHints));
    }

    [Fact]
    public void The_attachment_indexing_status_comes_first()
    {
        CommandCenterState state = new(new SessionLogBuffer())
        {
            SessionAttachments = [PendingAttachment()],
        };

        IReadOnlyList<string> items = state.FooterHintItems(modelSelectorVisible: true);

        Assert.Equal("Indexing attachments: 1 pending", items[0]);

        Assert.Equal(CommandCenterGuidance.ComposerHints(modelSelectorVisible: true), items.Skip(1));
    }

    [Fact]
    public void A_footer_message_that_is_set_wins_and_is_shown_alone()
    {
        CommandCenterState state = new(new SessionLogBuffer())
        {
            FooterHint = "Press Ctrl+Q to quit",
        };

        Assert.Equal("Press Ctrl+Q to quit", state.FooterHints);
    }

    [Fact]
    public void Without_a_message_the_plain_footer_joins_every_hint()
    {
        CommandCenterState state = new(new SessionLogBuffer());

        Assert.Equal(
            string.Join(CommandCenterHintBar.Separator, state.FooterHintItems(modelSelectorVisible: true)),
            state.FooterHints);
    }

    /// <summary>Each focus region and overlay teaches its own keys, not the composer's.</summary>
    [Fact]
    public void Each_region_and_overlay_has_its_own_hint_list()
    {
        (CommandCenterFocusRegion Focus, CommandCenterOverlayKind Overlay, IReadOnlyList<string> Expected)[] cases =
        [
            (CommandCenterFocusRegion.Composer, CommandCenterOverlayKind.None, CommandCenterGuidance.ComposerHints(modelSelectorVisible: true)),
            (CommandCenterFocusRegion.Sessions, CommandCenterOverlayKind.None, CommandCenterGuidance.SessionHints),
            (CommandCenterFocusRegion.Overlay, CommandCenterOverlayKind.SessionPicker, CommandCenterGuidance.SessionHints),
            (CommandCenterFocusRegion.Transcript, CommandCenterOverlayKind.None, CommandCenterGuidance.TranscriptHints),
            (CommandCenterFocusRegion.Incantations, CommandCenterOverlayKind.None, CommandCenterGuidance.IncantationsHints),
            (CommandCenterFocusRegion.Model, CommandCenterOverlayKind.None, CommandCenterGuidance.ModelControlHints),
            (CommandCenterFocusRegion.Overlay, CommandCenterOverlayKind.ModelPicker, CommandCenterGuidance.ModelPickerHints),
            (CommandCenterFocusRegion.Overlay, CommandCenterOverlayKind.CommandPalette, CommandCenterGuidance.CommandPaletteHints),
            (CommandCenterFocusRegion.Overlay, CommandCenterOverlayKind.Help, CommandCenterGuidance.HelpOverlayHints),
            (CommandCenterFocusRegion.Overlay, CommandCenterOverlayKind.QuitConfirm, CommandCenterGuidance.ConfirmHints),
            (CommandCenterFocusRegion.Overlay, CommandCenterOverlayKind.DiscardConfirm, CommandCenterGuidance.ConfirmHints),
            (CommandCenterFocusRegion.Overlay, CommandCenterOverlayKind.HumanPrompt, CommandCenterGuidance.HumanPromptHints),
        ];

        foreach ((CommandCenterFocusRegion focus, CommandCenterOverlayKind overlay, IReadOnlyList<string> expected) in cases)
        {
            CommandCenterState state = new(new SessionLogBuffer())
            {
                FocusRegion = focus,
                Overlay = overlay,
            };

            Assert.Equal(expected, state.FooterHintItems(modelSelectorVisible: true));
        }
    }

    private static IEnumerable<IReadOnlyList<string>> AllHintLists() =>
    [
        CommandCenterGuidance.ComposerHints(modelSelectorVisible: true),
        CommandCenterGuidance.ComposerHints(modelSelectorVisible: false),
        CommandCenterGuidance.SessionHints,
        CommandCenterGuidance.TranscriptHints,
        CommandCenterGuidance.IncantationsHints,
        CommandCenterGuidance.ModelControlHints,
        CommandCenterGuidance.ModelPickerHints,
        CommandCenterGuidance.CommandPaletteHints,
        CommandCenterGuidance.HelpOverlayHints,
        CommandCenterGuidance.ConfirmHints,
        CommandCenterGuidance.HumanPromptHints,
    ];

    private static SessionAttachmentDto PendingAttachment() =>
        new(
            Guid.NewGuid(),
            "notes",
            "notes.txt",
            1,
            "notes.txt",
            "text/plain",
            10,
            SessionAttachmentKind.Text,
            "sha256",
            DateTimeOffset.UtcNow,
            IndexingStatus: SessionAttachmentIndexStatus.Pending);
}
