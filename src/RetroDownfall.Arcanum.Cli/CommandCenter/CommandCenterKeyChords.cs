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
            IsBareLetter: !ctrl && !key.IsAlt && isLetter);
    }
}
