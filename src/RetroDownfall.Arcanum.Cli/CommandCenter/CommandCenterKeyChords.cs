using System.Text;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Turns a Terminal.Gui <see cref="Key"/> into the <see cref="KeyChord"/> flags
/// <see cref="CommandCenterKeymap"/> decides from. Kept apart from the host so the decoding can be
/// tested against Terminal.Gui's own decoders without a running terminal UI.
/// </summary>
internal static class CommandCenterKeyChords
{
    /// <summary>The printable ASCII character a key types, or <see langword="null"/> for any other key.</summary>
    public static char? TryGetPrintableChar(Key key)
    {
        try
        {
            if (key.TryGetPrintableRune(out Rune rune) && rune.IsAscii && !Rune.IsControl(rune))
            {
                return (char)rune.Value;
            }

            string grapheme = key.AsGrapheme;
            if (!string.IsNullOrEmpty(grapheme) && grapheme.Length == 1)
            {
                return grapheme[0];
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// The chord flags for <paramref name="key"/>. <see cref="KeyChord.IsNewLine"/> follows what
    /// Terminal.Gui 2.4.17 actually decodes, not the labels on the keys (each fact is pinned by
    /// <c>CommandCenterKeyDecodingTests</c>):
    /// <list type="bullet">
    /// <item><description>CR, which is what Enter sends everywhere and what Terminal.app, iTerm2's defaults,
    /// tmux and xterm.js also send for Ctrl+Enter, decodes to bare <see cref="Key.Enter"/>. That is the send
    /// key, so it is the one Enter that is not a line break.</description></item>
    /// <item><description>LF (Ctrl+J, tmux C-j) decodes to Enter with Ctrl through
    /// <c>EscSeqUtils.MapConsoleKeyInfo</c>, which turns KeyChar 10 into <see cref="ConsoleKey.Enter"/> with
    /// the control modifier. A CSI-u Ctrl+Enter decodes to the same key, so the two cannot be told apart and
    /// both insert a line break.</description></item>
    /// <item><description>ESC CR (Alt or Option+Enter, tmux M-Enter) and ESC LF decode to Ctrl+Alt+M and
    /// Ctrl+Alt+J through Terminal.Gui's internal Esc-as-Alt pattern, which returns the control character's
    /// letter with Ctrl and Alt, <c>((Key)(ch + 96)).WithCtrl.WithAlt</c>, and never Enter with Alt. They
    /// are line breaks but not Enter, so Sessions, the transcript and the model control do not act on
    /// them.</description></item>
    /// <item><description>CSI-u (kitty) <c>ESC[13;&lt;mod&gt;u</c> decodes to Enter plus that modifier:
    /// Shift+Enter, Alt+Enter or Ctrl+Enter.</description></item>
    /// <item><description>Under kitty's disambiguation mode Ctrl+J arrives as the letter, Ctrl+J, rather
    /// than as LF.</description></item>
    /// </list>
    /// </summary>
    public static KeyChord FromKey(Key key)
    {
        bool ctrl = key.IsCtrl;

        char? ch = TryGetPrintableChar(key);

        bool isLetter = ch is { } c && char.IsLetter(c);

        KeyCode baseCode = key.KeyCode
            & ~(KeyCode.ShiftMask | KeyCode.AltMask | KeyCode.CtrlMask);

        // Ctrl/Shift/Alt+Enter may arrive as WithCtrl/WithShift/WithAlt rather than bare Key.Enter.
        bool isEnter = key == Key.Enter
            || key == Key.Enter.WithShift
            || key == Key.Enter.WithAlt
            || key == Key.Enter.WithCtrl
            || baseCode == KeyCode.Enter;

        bool isNewLine = (baseCode == KeyCode.Enter && (key.IsCtrl || key.IsShift || key.IsAlt))
            || key == Key.M.WithCtrl.WithAlt
            || key == Key.J.WithCtrl.WithAlt
            || key == Key.J.WithCtrl;

        return new KeyChord(
            IsEnter: isEnter,
            IsEsc: key == Key.Esc,
            IsTab: key == Key.Tab || key == Key.Tab.WithShift,
            IsShift: key.IsShift,
            IsAlt: key.IsAlt,
            IsCtrl: key.IsCtrl,
            IsCtrlC: key == Key.C.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.C),
            IsCtrlK: key == Key.K.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.K),
            IsCtrlO: key == Key.O.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.O),
            IsCtrlN: key == Key.N.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.N),
            IsCtrlR: key == Key.R.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.R),
            IsCtrlQ: key == Key.Q.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.Q),
            IsCtrlT: key == Key.T.WithCtrl || (ctrl && (key.KeyCode & ~KeyCode.CtrlMask) == KeyCode.T),
            IsF1: key == Key.F1,
            IsF5: key == Key.F5,
            IsUp: key == Key.CursorUp,
            IsDown: key == Key.CursorDown,
            IsPageUp: baseCode == KeyCode.PageUp,
            IsPageDown: baseCode == KeyCode.PageDown,
            IsHome: key == Key.Home,
            IsEnd: key == Key.End,
            IsJ: !ctrl && ch is 'j' or 'J',
            IsK: !ctrl && ch is 'k' or 'K',
            IsSpace: !ctrl && !key.IsAlt && ch is ' ',
            IsBareLetter: !ctrl && !key.IsAlt && isLetter,
            IsNewLine: isNewLine);
    }
}
