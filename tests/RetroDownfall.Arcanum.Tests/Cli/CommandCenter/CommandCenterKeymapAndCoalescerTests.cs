using System.Threading.Channels;
using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterKeymapTests
{
    [Fact]
    public void CtrlC_while_streaming_cancels_turn()
    {
        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Composer,
            isStreaming: true,
            composerHasText: true,
            overlayOpen: false,
            new KeyChord(IsCtrlC: true));

        Assert.Equal(CommandCenterAction.CancelTurn, action);
    }

    [Fact]
    public void CtrlC_with_composer_text_clears_composer()
    {
        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Composer,
            isStreaming: false,
            composerHasText: true,
            overlayOpen: false,
            new KeyChord(IsCtrlC: true));

        Assert.Equal(CommandCenterAction.ClearComposer, action);
    }

    [Fact]
    public void CtrlC_empty_composer_shows_quit_hint()
    {
        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Composer,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: false,
            new KeyChord(IsCtrlC: true));

        Assert.Equal(CommandCenterAction.QuitHint, action);
    }

    [Theory]
    [InlineData(true, false, nameof(CommandCenterAction.Help))]
    [InlineData(false, true, nameof(CommandCenterAction.CommandPalette))]
    public void Global_chords_map(bool f1, bool ctrlK, string expected)
    {
        CommandCenterAction action = CommandCenterKeymap.Map(
            CommandCenterFocusRegion.Composer,
            isStreaming: false,
            composerHasText: false,
            overlayOpen: false,
            new KeyChord(IsF1: f1, IsCtrlK: ctrlK));

        Assert.Equal(Enum.Parse<CommandCenterAction>(expected), action);
    }

    [Fact]
    public void Sessions_jk_and_arrows_move_selection()
    {
        Assert.Equal(
            CommandCenterAction.SessionSelectDown,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Sessions,
                false,
                false,
                false,
                new KeyChord(IsBareLetter: true, IsJ: true)));

        Assert.Equal(
            CommandCenterAction.SessionSelectUp,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Sessions,
                false,
                false,
                false,
                new KeyChord(IsUp: true)));
    }

    [Fact]
    public void Enter_in_composer_falls_through_for_newline()
    {
        Assert.Equal(
            CommandCenterAction.None,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Composer,
                false,
                false,
                false,
                new KeyChord(IsEnter: true)));
    }

    [Fact]
    public void CtrlEnter_in_composer_sends()
    {
        Assert.Equal(
            CommandCenterAction.Send,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Composer,
                false,
                false,
                false,
                new KeyChord(IsEnter: true, IsCtrl: true)));
    }

    [Fact]
    public void ShiftEnter_in_composer_falls_through_for_newline()
    {
        Assert.Equal(
            CommandCenterAction.None,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Composer,
                false,
                false,
                false,
                new KeyChord(IsEnter: true, IsShift: true)));
    }

    [Fact]
    public void AltEnter_in_composer_falls_through_for_newline()
    {
        Assert.Equal(
            CommandCenterAction.None,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Composer,
                false,
                false,
                false,
                new KeyChord(IsEnter: true, IsAlt: true)));
    }

    [Fact]
    public void Esc_closes_overlay()
    {
        Assert.Equal(
            CommandCenterAction.CloseOverlayOrFocusComposer,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Overlay,
                false,
                false,
                overlayOpen: true,
                new KeyChord(IsEsc: true)));
    }

    [Fact]
    public void Esc_from_sessions_returns_to_composer()
    {
        Assert.Equal(
            CommandCenterAction.CloseOverlayOrFocusComposer,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Sessions,
                false,
                false,
                overlayOpen: false,
                new KeyChord(IsEsc: true)));
    }

    [Fact]
    public void Esc_in_empty_composer_is_noop_not_quit()
    {
        Assert.Equal(
            CommandCenterAction.NoOp,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Composer,
                false,
                composerHasText: false,
                overlayOpen: false,
                new KeyChord(IsEsc: true)));
    }

    [Fact]
    public void CtrlO_focuses_sessions()
    {
        Assert.Equal(
            CommandCenterAction.FocusSessions,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Composer,
                false,
                false,
                false,
                new KeyChord(IsCtrlO: true)));
    }

    [Theory]
    [InlineData(nameof(CommandCenterOverlayKind.Help), nameof(CommandCenterAction.CloseOverlayOrFocusComposer))]
    [InlineData(nameof(CommandCenterOverlayKind.SessionPicker), nameof(CommandCenterAction.ResumeSelectedSession))]
    [InlineData(nameof(CommandCenterOverlayKind.CommandPalette), nameof(CommandCenterAction.ExecutePaletteItem))]
    [InlineData(nameof(CommandCenterOverlayKind.QuitConfirm), nameof(CommandCenterAction.ConfirmPending))]
    [InlineData(nameof(CommandCenterOverlayKind.DiscardConfirm), nameof(CommandCenterAction.ConfirmPending))]
    [InlineData(nameof(CommandCenterOverlayKind.None), nameof(CommandCenterAction.NoOp))]
    public void Overlay_Enter_is_explicit_by_kind(string kindName, string expectedName)
    {
        var kind = Enum.Parse<CommandCenterOverlayKind>(kindName);
        var expected = Enum.Parse<CommandCenterAction>(expectedName);
        Assert.Equal(expected, CommandCenterKeymap.MapOverlayEnter(kind));
    }

    [Fact]
    public void Overlay_focus_Enter_via_Map_does_not_resume()
    {
        Assert.Equal(
            CommandCenterAction.None,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Overlay,
                false,
                false,
                overlayOpen: true,
                new KeyChord(IsEnter: true)));
    }

    [Fact]
    public void Sessions_focus_Enter_still_resumes()
    {
        Assert.Equal(
            CommandCenterAction.ResumeSelectedSession,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Sessions,
                false,
                false,
                overlayOpen: false,
                new KeyChord(IsEnter: true)));
    }
}

public sealed class StreamingUiCoalescerTests
{
    [Fact]
    public async Task NoteToken_without_newline_or_interval_buffers()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);

        await coalescer.NoteTokenAsync("hello");
        Assert.True(coalescer.HasPending);
        Assert.Equal(0, coalescer.FlushCount);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task NoteToken_with_newline_flushes_immediately()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        await using StreamingUiCoalescer coalescer = new(channel.Writer);

        await coalescer.NoteTokenAsync("line\n");
        Assert.False(coalescer.HasPending);
        Assert.Equal(1, coalescer.FlushCount);
        Assert.True(channel.Reader.TryRead(out CommandCenterUiUpdate? update));
        Assert.Equal(CommandCenterUiUpdateKind.RefreshLog, update!.Kind);
    }

    [Fact]
    public async Task Interval_elapsed_flushes_on_next_token()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);

        await coalescer.NoteTokenAsync("a");
        now = now.AddMilliseconds(60);
        await coalescer.NoteTokenAsync("b");

        Assert.Equal(1, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);
    }

    [Fact]
    public async Task FlushBeforeBlock_and_FlushFinal_drain_pending()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        await using StreamingUiCoalescer coalescer = new(channel.Writer);

        await coalescer.NoteTokenAsync("pending");
        await coalescer.FlushBeforeBlockAsync();
        Assert.Equal(1, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);

        await coalescer.NoteTokenAsync("more");
        await coalescer.FlushFinalAsync();
        Assert.Equal(2, coalescer.FlushCount);
    }

    [Fact]
    public async Task FlushCancelled_and_Dispose_never_drop_final_partial()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        StreamingUiCoalescer coalescer = new(channel.Writer);

        await coalescer.NoteTokenAsync("partial");
        await coalescer.FlushCancelledAsync();
        Assert.Equal(1, coalescer.FlushCount);

        await coalescer.NoteTokenAsync("again");
        await coalescer.DisposeAsync();
        Assert.Equal(2, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);
    }
}
