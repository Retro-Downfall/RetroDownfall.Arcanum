namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Focus region for Command Center keyboard routing.</summary>
internal enum CommandCenterFocusRegion
{
    Composer,
    Transcript,
    Sessions,
    Overlay,
    Incantations,
}

/// <summary>Logical actions produced by the Command Center keymap.</summary>
internal enum CommandCenterAction
{
    None,
    NoOp,
    Send,
    InsertComposerNewLine,
    CancelTurn,
    ClearComposer,
    QuitHint,
    Quit,
    Help,
    CommandPalette,
    FocusSessions,
    NewSession,
    Refresh,
    CycleFocusNext,
    CycleFocusPrev,
    ToggleTelemetryPane,
    CloseOverlayOrFocusComposer,
    SessionSelectUp,
    SessionSelectDown,
    ResumeSelectedSession,
    ExecutePaletteItem,
    ConfirmPending,
    ScrollTranscriptUp,
    ScrollTranscriptDown,
    PageTranscriptUp,
    PageTranscriptDown,
    JumpTranscriptHome,
    JumpTranscriptEnd,
    ScrollIncantationsUp,
    ScrollIncantationsDown,
    PageIncantationsUp,
    PageIncantationsDown,
    JumpIncantationsHome,
    JumpIncantationsEnd,
}

/// <summary>
/// Pure key → action mapping for Command Center. Terminal.Gui key codes are passed as
/// normalized flags so this type stays unit-testable without a TUI host.
/// </summary>
internal static class CommandCenterKeymap
{
    public static CommandCenterAction Map(
        CommandCenterFocusRegion focus,
        bool isStreaming,
        bool composerHasText,
        bool overlayOpen,
        KeyChord chord)
    {
        if (chord.IsF1)
        {
            return CommandCenterAction.Help;
        }

        if (chord.IsCtrlK)
        {
            return CommandCenterAction.CommandPalette;
        }

        if (chord.IsCtrlO)
        {
            return CommandCenterAction.FocusSessions;
        }

        if (chord.IsCtrlN)
        {
            return CommandCenterAction.NewSession;
        }

        if (chord.IsCtrlR || chord.IsF5)
        {
            return CommandCenterAction.Refresh;
        }

        if (chord.IsCtrlT)
        {
            return CommandCenterAction.ToggleTelemetryPane;
        }

        if (chord.IsCtrlQ)
        {
            return CommandCenterAction.Quit;
        }

        if (chord.IsCtrlC)
        {
            if (isStreaming)
            {
                return CommandCenterAction.CancelTurn;
            }

            if (composerHasText)
            {
                return CommandCenterAction.ClearComposer;
            }

            return CommandCenterAction.QuitHint;
        }

        if (chord.IsEsc)
        {
            if (overlayOpen || focus == CommandCenterFocusRegion.Overlay)
            {
                return CommandCenterAction.CloseOverlayOrFocusComposer;
            }

            if (isStreaming)
            {
                return CommandCenterAction.CancelTurn;
            }

            if (focus is CommandCenterFocusRegion.Transcript or CommandCenterFocusRegion.Sessions)
            {
                return CommandCenterAction.CloseOverlayOrFocusComposer;
            }

            if (composerHasText)
            {
                return CommandCenterAction.ClearComposer;
            }

            // Never let Esc fall through to Terminal.Gui (would quit the app).
            return CommandCenterAction.NoOp;
        }

        if (chord.IsTab)
        {
            // Modal overlays own the keyboard; don't cycle panes (would FocusInput and dismiss).
            if (overlayOpen || focus == CommandCenterFocusRegion.Overlay)
            {
                return CommandCenterAction.NoOp;
            }

            return chord.IsShift ? CommandCenterAction.CycleFocusPrev : CommandCenterAction.CycleFocusNext;
        }

        if (focus == CommandCenterFocusRegion.Composer && chord.IsEnter)
        {
            // Ctrl+Enter sends. Bare/Shift/Alt+Enter fall through so TextView can insert
            // newlines (EnterKeyAddsLine must stay true for WordWrap).
            if (chord.IsCtrl)
            {
                return CommandCenterAction.Send;
            }

            return CommandCenterAction.None;
        }

        if (focus == CommandCenterFocusRegion.Sessions)
        {
            if (chord.IsEnter)
            {
                return CommandCenterAction.ResumeSelectedSession;
            }

            if (chord.IsUp || (chord.IsBareLetter && chord.IsK))
            {
                return CommandCenterAction.SessionSelectUp;
            }

            if (chord.IsDown || (chord.IsBareLetter && chord.IsJ))
            {
                return CommandCenterAction.SessionSelectDown;
            }
        }

        // Overlay Enter is routed via MapOverlayEnter(OverlayKind) — never ResumeSelected by default.
        if (focus == CommandCenterFocusRegion.Overlay)
        {
            if (chord.IsUp || (chord.IsBareLetter && chord.IsK))
            {
                return CommandCenterAction.SessionSelectUp;
            }

            if (chord.IsDown || (chord.IsBareLetter && chord.IsJ))
            {
                return CommandCenterAction.SessionSelectDown;
            }

            if (chord.IsEnter)
            {
                return CommandCenterAction.None;
            }
        }

        if (focus == CommandCenterFocusRegion.Transcript)
        {
            if (chord.IsUp)
            {
                return CommandCenterAction.ScrollTranscriptUp;
            }

            if (chord.IsDown)
            {
                return CommandCenterAction.ScrollTranscriptDown;
            }

            if (chord.IsPageUp)
            {
                return CommandCenterAction.PageTranscriptUp;
            }

            if (chord.IsPageDown)
            {
                return CommandCenterAction.PageTranscriptDown;
            }

            if (chord.IsHome)
            {
                return CommandCenterAction.JumpTranscriptHome;
            }

            if (chord.IsEnd)
            {
                return CommandCenterAction.JumpTranscriptEnd;
            }

            if (chord.IsEnter)
            {
                return CommandCenterAction.ResumeSelectedSession;
            }
        }

        if (focus == CommandCenterFocusRegion.Incantations)
        {
            if (chord.IsUp)
            {
                return CommandCenterAction.ScrollIncantationsUp;
            }

            if (chord.IsDown)
            {
                return CommandCenterAction.ScrollIncantationsDown;
            }

            if (chord.IsPageUp)
            {
                return CommandCenterAction.PageIncantationsUp;
            }

            if (chord.IsPageDown)
            {
                return CommandCenterAction.PageIncantationsDown;
            }

            if (chord.IsHome)
            {
                return CommandCenterAction.JumpIncantationsHome;
            }

            if (chord.IsEnd)
            {
                return CommandCenterAction.JumpIncantationsEnd;
            }
        }

        if (focus == CommandCenterFocusRegion.Composer)
        {
            if (chord.IsPageUp)
            {
                return CommandCenterAction.PageTranscriptUp;
            }

            if (chord.IsPageDown)
            {
                return CommandCenterAction.PageTranscriptDown;
            }
        }

        return CommandCenterAction.None;
    }

    /// <summary>
    /// Enter while an overlay is open — explicit by <see cref="CommandCenterOverlayKind"/>.
    /// Help never resumes a session.
    /// </summary>
    public static CommandCenterAction MapOverlayEnter(CommandCenterOverlayKind kind) =>
        kind switch
        {
            CommandCenterOverlayKind.Help => CommandCenterAction.CloseOverlayOrFocusComposer,
            CommandCenterOverlayKind.SessionPicker => CommandCenterAction.ResumeSelectedSession,
            CommandCenterOverlayKind.CommandPalette => CommandCenterAction.ExecutePaletteItem,
            CommandCenterOverlayKind.QuitConfirm or CommandCenterOverlayKind.DiscardConfirm
                or CommandCenterOverlayKind.WardConfirm
                => CommandCenterAction.ConfirmPending,
            // HumanPrompt: Enter inserts newline in the answer TextView; Ctrl+Enter submits.
            CommandCenterOverlayKind.HumanPrompt => CommandCenterAction.NoOp,
            _ => CommandCenterAction.NoOp,
        };
}

/// <summary>Normalized key chord flags for <see cref="CommandCenterKeymap"/>.</summary>
internal readonly record struct KeyChord(
    bool IsEnter = false,
    bool IsEsc = false,
    bool IsTab = false,
    bool IsShift = false,
    bool IsAlt = false,
    bool IsCtrl = false,
    bool IsCtrlC = false,
    bool IsCtrlK = false,
    bool IsCtrlO = false,
    bool IsCtrlN = false,
    bool IsCtrlR = false,
    bool IsCtrlQ = false,
    bool IsCtrlT = false,
    bool IsF1 = false,
    bool IsF5 = false,
    bool IsUp = false,
    bool IsDown = false,
    bool IsPageUp = false,
    bool IsPageDown = false,
    bool IsHome = false,
    bool IsEnd = false,
    bool IsJ = false,
    bool IsK = false,
    bool IsBareLetter = false);
