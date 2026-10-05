using System.Runtime.CompilerServices;
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

    /// <summary>
    /// The palette is not the sessions list: driving the session selection from it clamps movement
    /// to the session count, hides every entry past it, and silently repoints the resume target.
    /// </summary>
    [Theory]
    [InlineData(true, false, nameof(CommandCenterAction.PaletteSelectUp))]
    [InlineData(false, true, nameof(CommandCenterAction.PaletteSelectDown))]
    internal void Palette_arrows_move_the_palette_not_the_session_selection(
        bool up,
        bool down,
        string expected)
    {
        Assert.Equal(
            Enum.Parse<CommandCenterAction>(expected),
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Overlay,
                false,
                false,
                overlayOpen: true,
                new KeyChord(IsUp: up, IsDown: down),
                CommandCenterOverlayKind.CommandPalette));
    }

    /// <summary>An overlay with no selectable rows must not move somebody else's selection either.</summary>
    [Fact]
    internal void Help_overlay_arrows_do_not_move_the_session_selection()
    {
        Assert.Equal(
            CommandCenterAction.None,
            CommandCenterKeymap.Map(
                CommandCenterFocusRegion.Overlay,
                false,
                false,
                overlayOpen: true,
                new KeyChord(IsDown: true),
                CommandCenterOverlayKind.Help));
    }
}

public sealed class StreamingUiCoalescerTests
{
    [Fact]
    public async Task NoteToken_within_the_interval_buffers()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);

        await coalescer.NoteTokenAsync();
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

        await coalescer.NoteTokenAsync();
        await coalescer.NoteTokenAsync();
        Assert.Equal(0, snapshots);

        await coalescer.FlushFinalAsync();
        Assert.Equal(1, snapshots);
        Assert.Equal(1, coalescer.FlushCount);
    }

    [Fact]
    public async Task The_first_chunk_after_the_interval_has_elapsed_since_creation_flushes()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);

        now = now.AddMilliseconds(60);
        await coalescer.NoteTokenAsync();

        Assert.False(coalescer.HasPending);
        Assert.Equal(1, coalescer.FlushCount);
        Assert.True(channel.Reader.TryRead(out CommandCenterUiUpdate? update));
        Assert.Equal(CommandCenterUiUpdateKind.RefreshLog, update!.Kind);
    }

    /// <summary>
    /// Each flush copies the whole answer and re-wraps its entry on the UI thread, so a flush per line
    /// made a line-heavy answer (a log, a table, a code block) cost one rebuild per line. The coalescer is
    /// told a chunk arrived and never what it contained, so a newline cannot bypass the interval: a burst
    /// of chunks inside it is held until it elapses.
    /// </summary>
    [Fact]
    public async Task A_burst_of_chunks_inside_the_flush_interval_is_held_until_it_elapses()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);

        for (int i = 0; i < 100; i++)
        {
            await coalescer.NoteTokenAsync();
            now = now.AddTicks(TimeSpan.TicksPerMillisecond / 100);
        }

        Assert.Equal(0, coalescer.FlushCount);
        Assert.True(coalescer.HasPending);
        Assert.False(channel.Reader.TryRead(out _));

        now = now.AddMilliseconds(60);
        await coalescer.NoteTokenAsync();

        Assert.Equal(1, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);
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

        await coalescer.NoteTokenAsync();
        now = now.AddMilliseconds(60);
        await coalescer.NoteTokenAsync();

        Assert.Equal(1, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);
    }

    [Fact]
    public async Task FlushBeforeBlock_and_FlushFinal_drain_pending()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        await using StreamingUiCoalescer coalescer = new(channel.Writer);

        await coalescer.NoteTokenAsync();
        await coalescer.FlushBeforeBlockAsync();
        Assert.Equal(1, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);

        await coalescer.NoteTokenAsync();
        await coalescer.FlushFinalAsync();
        Assert.Equal(2, coalescer.FlushCount);
    }

    /// <summary>
    /// The flush that follows a cancellation is the one that gets the cut-off text onto the screen. It
    /// takes no token: it runs because the turn's token is already cancelled, so the write it makes must
    /// not depend on any token at all (the UI channel is unbounded, so it completes at once).
    /// </summary>
    [Fact]
    public async Task FlushCancelled_writes_the_pending_refresh()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);

        await coalescer.NoteTokenAsync();
        Assert.True(coalescer.HasPending);

        await coalescer.FlushCancelledAsync();

        Assert.Equal(1, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);
        Assert.True(channel.Reader.TryRead(out CommandCenterUiUpdate? update));
        Assert.Equal(CommandCenterUiUpdateKind.RefreshLog, update!.Kind);
    }

    [Fact]
    public async Task FlushCancelled_and_Dispose_never_drop_final_partial()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        // A frozen clock: with the real one, a slow machine could cross the 50 ms interval between the two
        // chunks and flush early, which changes the counts this test pins.
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now);

        await coalescer.NoteTokenAsync();
        await coalescer.FlushCancelledAsync();
        Assert.Equal(1, coalescer.FlushCount);

        await coalescer.NoteTokenAsync();
        await coalescer.DisposeAsync();
        Assert.Equal(2, coalescer.FlushCount);
        Assert.False(coalescer.HasPending);
    }

    /// <summary>
    /// A chunk held back by the cadence is only flushed by the next chunk, block or final flush, so a model
    /// that pauses right after a burst left its last words off the screen for as long as it paused. The
    /// stream wrapper flushes what is pending when the wait for the next event outlasts the interval.
    /// </summary>
    [Fact]
    public async Task A_pending_chunk_is_flushed_when_the_stream_stalls_for_the_rest_of_the_interval()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        ManualDelay delay = new();
        TaskCompletionSource end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now,
            delay: delay.Delay);

        Task consumer = Task.Run(async () =>
        {
            await foreach (int _ in coalescer.WithTrailingFlushAsync(OneItemThenAStall(end)))
            {
                await coalescer.NoteTokenAsync();
            }
        });

        TimeSpan waitingFor = await delay.FirstArmed.WaitAsync(AsyncTestTimeout);

        Assert.Equal(TimeSpan.FromMilliseconds(50), waitingFor);
        Assert.True(coalescer.HasPending);
        Assert.Equal(0, coalescer.FlushCount);

        now = now.AddMilliseconds(50);
        delay.ElapseAll();

        CommandCenterUiUpdate update = await channel.Reader.ReadAsync().AsTask().WaitAsync(AsyncTestTimeout);
        Assert.Equal(CommandCenterUiUpdateKind.RefreshLog, update.Kind);
        Assert.False(coalescer.HasPending);

        end.SetResult();
        await consumer.WaitAsync(AsyncTestTimeout);

        Assert.Equal(1, coalescer.FlushCount);
    }

    [Fact]
    public async Task A_chunk_the_next_event_flushes_first_is_not_flushed_again_by_the_timer()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        ManualDelay delay = new();
        TaskCompletionSource secondItem = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now,
            delay: delay.Delay);

        Task consumer = Task.Run(async () =>
        {
            await foreach (int _ in coalescer.WithTrailingFlushAsync(TwoItemsWithAStallBetween(secondItem)))
            {
                await coalescer.NoteTokenAsync();
            }
        });

        _ = await delay.FirstArmed.WaitAsync(AsyncTestTimeout);

        // The next event arrives before the timer does, and by then the interval has elapsed, so noting it
        // flushes everything that was pending.
        now = now.AddMilliseconds(60);
        secondItem.SetResult();
        await consumer.WaitAsync(AsyncTestTimeout);

        delay.ElapseAll();

        Assert.True(delay.FirstWasCancelled);
        Assert.Equal(1, coalescer.FlushCount);
    }

    /// <summary>
    /// A cancelled wait must end with the cancellation, and must not give the read up while it is still
    /// running: disposing an async iterator in the middle of its <c>MoveNextAsync</c> throws, and that
    /// exception would replace the cancellation. The read here ignores its token until the test releases it,
    /// as a read stalled inside the transport does.
    /// </summary>
    [Fact]
    public async Task A_cancelled_wait_ends_with_the_cancellation_once_the_outstanding_read_finishes()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        DateTimeOffset now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        ManualDelay delay = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource run = new();
        await using StreamingUiCoalescer coalescer = new(
            channel.Writer,
            flushInterval: TimeSpan.FromMilliseconds(50),
            utcNow: () => now,
            delay: delay.Delay);

        Task consumer = Task.Run(async () =>
        {
            await foreach (int _ in coalescer.WithTrailingFlushAsync(OneItemThenAReadThatIgnoresCancellation(release), run.Token))
            {
                await coalescer.NoteTokenAsync(run.Token);
            }
        });

        _ = await delay.FirstArmed.WaitAsync(AsyncTestTimeout);
        run.Cancel();

        // The read is still outstanding, so the stream has not ended yet. (A wait that gave it up would
        // already have thrown from disposing the iterator under it.)
        Task first = await Task.WhenAny(consumer, Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.NotSame(consumer, first);

        release.SetResult();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.WaitAsync(AsyncTestTimeout));

        // What was pending stays pending for the caller's own cancelled-turn flush; the timer did not take it.
        Assert.True(coalescer.HasPending);
        Assert.Equal(0, coalescer.FlushCount);
    }

    private static async IAsyncEnumerable<int> OneItemThenAStall(
        TaskCompletionSource end,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 1;
        await end.Task.WaitAsync(cancellationToken);
    }

    private static async IAsyncEnumerable<int> OneItemThenAReadThatIgnoresCancellation(
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 1;
        await release.Task;
        cancellationToken.ThrowIfCancellationRequested();
        yield return 2;
    }

    private static async IAsyncEnumerable<int> TwoItemsWithAStallBetween(
        TaskCompletionSource secondItem,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 1;
        await secondItem.Task.WaitAsync(cancellationToken);
        yield return 2;
    }

    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stands in for the flush timer so a test decides when the interval "elapses" instead of racing a
    /// real clock. A delay completes only when the test says so, or is cancelled with its token.
    /// </summary>
    private sealed class ManualDelay
    {
        private readonly TaskCompletionSource<TimeSpan> _firstArmed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<TaskCompletionSource> _delays = [];

        public Task<TimeSpan> FirstArmed => _firstArmed.Task;

        public bool FirstWasCancelled
        {
            get
            {
                lock (_delays)
                {
                    return _delays[0].Task.IsCanceled;
                }
            }
        }

        public Task Delay(TimeSpan interval, CancellationToken cancellationToken)
        {
            TaskCompletionSource delay = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = cancellationToken.Register(() => delay.TrySetCanceled(cancellationToken));

            lock (_delays)
            {
                _delays.Add(delay);
            }

            _ = _firstArmed.TrySetResult(interval);
            return delay.Task;
        }

        public void ElapseAll()
        {
            lock (_delays)
            {
                foreach (TaskCompletionSource delay in _delays)
                {
                    _ = delay.TrySetResult();
                }
            }
        }
    }
}
