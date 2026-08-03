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
    public void CtrlPage_keys_request_adjacent_transcript_pages()
    {

        Assert.Equal(
            CommandCenterAction.LoadOlderTranscriptPage,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Transcript,
                false,
                false,
                false,
                new KeyChord(IsCtrl: true, IsPageUp: true)));

        Assert.Equal(
            CommandCenterAction.LoadNewerTranscriptPage,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Transcript,
                false,
                false,
                false,
                new KeyChord(IsCtrl: true, IsPageDown: true)));

        Assert.Equal(
            CommandCenterAction.LoadOlderSessionPage,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Sessions,
                false,
                false,
                false,
                new KeyChord(IsCtrl: true, IsPageDown: true)));

        Assert.Equal(
            CommandCenterAction.LoadNewerSessionPage,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Sessions,
                false,
                false,
                false,
                new KeyChord(IsCtrl: true, IsPageUp: true)));

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
    [InlineData(nameof(CommandCenterOverlayKind.WardConfirm), nameof(CommandCenterAction.ConfirmPending))]
    [InlineData(nameof(CommandCenterOverlayKind.HumanPrompt), nameof(CommandCenterAction.NoOp))]
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
    public void Tab_with_overlay_open_is_noop()
    {
        Assert.Equal(
            CommandCenterAction.NoOp,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Composer,
                false,
                false,
                overlayOpen: true,
                new KeyChord(IsTab: true)));
    }

    [Fact]
    public void OverlayLayout_MeasureHeight_fits_short_confirm()
    {
        int height = OverlayLayout.MeasureHeight(
            contentRows: 7,
            showFilter: false,
            bodyHeight: 40,
            terminalRows: 50,
            headerHeight: 3);

        Assert.Equal(9, height); // 2 border + 7 lines
        Assert.True(height < 25);
    }

    [Fact]
    public void OverlayLayout_MeasureWidth_grows_with_content()
    {
        Assert.Equal(60, OverlayLayout.MeasureWidth(200, contentColumns: 10));
        Assert.Equal(82, OverlayLayout.MeasureWidth(200, contentColumns: 80));
        Assert.Equal(120, OverlayLayout.MeasureWidth(200, contentColumns: 200));
        Assert.Equal(76, OverlayLayout.MeasureWidth(80, contentColumns: 200)); // terminal - 4
    }

    [Fact]
    public void OverlayLayout_WrapLines_expands_then_truncates_preview()
    {
        string longLine = new('x', 201);
        List<string> wrapped = OverlayLayout.WrapLines(["head", longLine, "tail"], innerWidth: 50);

        Assert.Equal("head", wrapped[0]);
        Assert.Equal("tail", wrapped[^1]);
        int previewRows = wrapped.Count - 2; // exclude head/tail
        Assert.Equal(OverlayLayout.MaxWrappedPreviewRows, previewRows);
        Assert.EndsWith("…", wrapped[^2]);
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
    public async Task Snapshot_callback_runs_only_when_a_refresh_is_flushed()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        int snapshots = 0;
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now,
            beforeFlush: () => snapshots++);

        await coalescer.NoteTokenAsync("a");
        await coalescer.NoteTokenAsync("b");
        Assert.Equal(0, snapshots);

        await coalescer.FlushFinalAsync();
        Assert.Equal(1, snapshots);
        Assert.Equal(1, coalescer.FlushCount);
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
