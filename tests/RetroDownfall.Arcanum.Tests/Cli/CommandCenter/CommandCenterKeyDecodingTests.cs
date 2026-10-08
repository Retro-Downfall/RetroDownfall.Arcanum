using RetroDownfall.Arcanum.Cli.CommandCenter;

using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Pins, through Terminal.Gui's own public decoders, which <see cref="Key"/> each Enter-like keystroke
/// becomes, and what the Command Center keymap makes of it. The rule (bare Enter sends, every other
/// Enter-like chord inserts a line break) rests on these facts rather than on the labels on the keys,
/// so a Terminal.Gui upgrade that changes one of them fails here instead of in an operator's terminal.
/// </summary>
public sealed class CommandCenterKeyDecodingTests
{
    /// <summary>
    /// The escape sequences a terminal sends for Enter with a modifier, and the key each decodes to.
    /// </summary>
    public static TheoryData<string, KeyCode> NewLineSequences => new()
    {
        // Alt/Option+Enter where the terminal sends Esc for Alt, and tmux M-Enter: ESC CR. Terminal.Gui's
        // Esc-as-Alt pattern turns the control character into its letter with Ctrl and Alt, never into
        // Enter with Alt.
        { "\u001b\r", Key.M.WithCtrl.WithAlt.KeyCode },

        // ESC LF: Alt+Ctrl+J, for the same reason.
        { "\u001b\n", Key.J.WithCtrl.WithAlt.KeyCode },

        // CSI-u (kitty) key reporting: Enter with the modifier it was pressed with.
        { "\u001b[13;5u", Key.Enter.WithCtrl.KeyCode },
        { "\u001b[13;2u", Key.Enter.WithShift.KeyCode },
        { "\u001b[13;3u", Key.Enter.WithAlt.KeyCode },

        // Kitty's disambiguation mode reports Ctrl+J as the letter rather than as a line feed.
        { "\u001b[106;5u", Key.J.WithCtrl.KeyCode },
    };

    /// <summary>
    /// CR is what Enter sends in every terminal, and also what Terminal.app, iTerm2's defaults, tmux and
    /// xterm.js send for Ctrl+Enter, which is why Ctrl+Enter could never be the only way to send.
    /// </summary>
    [Fact]
    public void A_carriage_return_decodes_to_plain_Enter()
    {
        ConsoleKeyInfo info = EscSeqUtils.MapChar('\r');

        Assert.Equal(ConsoleKey.Enter, info.Key);

        Assert.Equal((ConsoleModifiers)0, info.Modifiers);

        Assert.Equal(13, (int)KeyCode.Enter);

        Assert.Equal(Key.Enter.KeyCode, ConsoleKeyMapping.MapConsoleKeyInfoToKeyCode(info));
    }

    /// <summary>LF is Ctrl+J (and tmux C-j), and Terminal.Gui reports it as Ctrl+Enter.</summary>
    [Fact]
    public void A_line_feed_decodes_to_Ctrl_Enter()
    {
        ConsoleKeyInfo info = EscSeqUtils.MapChar('\n');

        Assert.Equal(ConsoleKey.Enter, info.Key);

        Assert.Equal(ConsoleModifiers.Control, info.Modifiers);

        Assert.Equal(Key.Enter.WithCtrl.KeyCode, ConsoleKeyMapping.MapConsoleKeyInfoToKeyCode(info));
    }

    /// <summary>
    /// xterm's modifyOtherKeys form of Ctrl+Enter is not decoded at all, so no rule can depend on it.
    /// </summary>
    [Fact]
    public void The_modify_other_keys_form_of_Ctrl_Enter_is_not_decoded()
    {
        AnsiKeyboardParser parser = new();

        Assert.Null(parser.IsKeyboard("\u001b[27;5;13~", isLastMinute: false));

        Assert.Null(parser.IsKeyboard("\u001b[27;5;13~", isLastMinute: true));
    }

    [Theory]
    [MemberData(nameof(NewLineSequences))]
    public void A_modified_Enter_sequence_decodes_to_its_documented_key_and_inserts_a_line_break(
        string sequence,
        KeyCode expected)
    {
        Key key = Decode(sequence);

        Assert.Equal(expected, key.KeyCode);

        Assert.True(InsertsComposerLineBreak(key), $"{key} should insert a line break.");
    }

    /// <summary>Ctrl+J is a line feed in every terminal, so it is the newline key the hints teach.</summary>
    [Fact]
    public void A_line_feed_inserts_a_composer_line_break()
    {
        Key key = new(ConsoleKeyMapping.MapConsoleKeyInfoToKeyCode(EscSeqUtils.MapChar('\n')));

        Assert.True(InsertsComposerLineBreak(key));
    }

    [Fact]
    public void A_carriage_return_sends_the_composer()
    {
        Key key = new(ConsoleKeyMapping.MapConsoleKeyInfoToKeyCode(EscSeqUtils.MapChar('\r')));

        Assert.True(SendsComposer(key));
    }

    [Fact]
    public void Bare_Enter_is_Enter_and_not_a_line_break()
    {
        KeyChord chord = CommandCenterKeyChords.FromKey(Key.Enter);

        Assert.True(chord.IsEnter);

        Assert.False(chord.IsNewLine);
    }

    [Fact]
    public void Enter_with_any_modifier_is_Enter_and_a_line_break()
    {
        foreach (Key key in new[] { Key.Enter.WithCtrl, Key.Enter.WithShift, Key.Enter.WithAlt })
        {
            KeyChord chord = CommandCenterKeyChords.FromKey(key);

            Assert.True(chord.IsEnter, $"{key} should still be Enter.");

            Assert.True(chord.IsNewLine, $"{key} should be a line break.");
        }
    }

    /// <summary>
    /// ESC CR and ESC LF are line breaks, but they are not Enter: Sessions, Transcript and the Model
    /// control treat Enter as their own action, and an Alt+Enter must not trigger one of those.
    /// </summary>
    [Fact]
    public void Esc_prefixed_Enter_is_a_line_break_but_not_Enter()
    {
        foreach (Key key in new[] { Key.M.WithCtrl.WithAlt, Key.J.WithCtrl.WithAlt })
        {
            KeyChord chord = CommandCenterKeyChords.FromKey(key);

            Assert.True(chord.IsNewLine, $"{key} should be a line break.");

            Assert.False(chord.IsEnter, $"{key} should not be Enter.");
        }
    }

    [Fact]
    public void Ctrl_J_is_a_line_break_and_a_bare_j_is_a_letter()
    {
        Assert.True(CommandCenterKeyChords.FromKey(Key.J.WithCtrl).IsNewLine);

        KeyChord bareJ = CommandCenterKeyChords.FromKey(Key.J);

        Assert.True(bareJ.IsJ);

        Assert.False(bareJ.IsNewLine);
    }

    [Fact]
    public void Ctrl_M_alone_is_not_a_line_break()
    {
        Assert.False(CommandCenterKeyChords.FromKey(Key.M.WithCtrl).IsNewLine);
    }

    /// <summary>The ask_human answer follows the composer: Enter submits.</summary>
    [Fact]
    public void Enter_in_the_ask_human_prompt_submits()
    {
        Assert.Equal(
            CommandCenterAction.Send,
            CommandCenterKeymap.MapOverlayEnter(CommandCenterOverlayKind.HumanPrompt));
    }

    /// <summary>
    /// Terminal.Gui tries the escape-sequence patterns while a sequence is still arriving and its
    /// Esc-as-Alt pattern only once the sequence has stopped growing, so the decode tries them in that
    /// order.
    /// </summary>
    private static Key Decode(string sequence)
    {
        AnsiKeyboardParser parser = new();

        AnsiKeyboardParserPattern? pattern = parser.IsKeyboard(sequence, isLastMinute: false)
            ?? parser.IsKeyboard(sequence, isLastMinute: true);

        Assert.NotNull(pattern);

        Key? key = pattern.GetKey(sequence);

        Assert.NotNull(key);

        return key;
    }

    private static bool InsertsComposerLineBreak(Key key) =>
        MapInComposer(key) == CommandCenterAction.InsertComposerNewLine;

    private static bool SendsComposer(Key key) => MapInComposer(key) == CommandCenterAction.Send;

    private static CommandCenterAction MapInComposer(Key key) =>
        CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Composer,
            isStreaming: false,
            composerHasText: true,
            overlayOpen: false,
            CommandCenterKeyChords.FromKey(key));
}
