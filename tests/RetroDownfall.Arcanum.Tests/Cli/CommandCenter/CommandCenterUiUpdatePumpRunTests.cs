using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The loop that carries queued UI updates to the Terminal.Gui thread. A failure inside it used to be
/// swallowed by a comment that said the run's own failure had already reported it, which nothing did.
/// </summary>
public sealed class CommandCenterUiUpdatePumpRunTests
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Separately_arriving_refreshes_fold_while_the_ui_callback_is_still_queued()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        ObservedReader reader = new(channel.Reader);

        ConcurrentQueue<Action> callbacks = new();

        List<CommandCenterUiUpdateKind> applied = [];

        Task pump = CommandCenterUiUpdatePump.RunAsync(reader, callbacks.Enqueue, applied.Add,
            new TestCapturingLogger<CommandCenterUiUpdatePumpRunTests>(), CancellationToken.None);

        for (int count = 1; count <= 30; count++)
        {
            await channel.Writer.WriteAsync(new(CommandCenterUiUpdateKind.RefreshLog));

            await reader.WaitUntilIdleAfterAsync(count);
        }

        channel.Writer.Complete();

        await pump.WaitAsync(AsyncTestTimeout);

        Assert.Single(callbacks)();

        Assert.Equal([CommandCenterUiUpdateKind.RefreshLog], applied);
    }

    [Fact]
    public async Task Pending_refreshes_widen_without_crossing_ordered_focus_actions()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        ObservedReader reader = new(channel.Reader);

        ConcurrentQueue<Action> callbacks = new();

        List<CommandCenterUiUpdateKind> applied = [];

        Task pump = CommandCenterUiUpdatePump.RunAsync(reader, callbacks.Enqueue, applied.Add,
            new TestCapturingLogger<CommandCenterUiUpdatePumpRunTests>(), CancellationToken.None);

        CommandCenterUiUpdateKind[] input = [CommandCenterUiUpdateKind.RefreshLog,
            CommandCenterUiUpdateKind.RefreshFooter, CommandCenterUiUpdateKind.FocusSessions,
            CommandCenterUiUpdateKind.RefreshSidebar, CommandCenterUiUpdateKind.RefreshLog,
            CommandCenterUiUpdateKind.FocusInput, CommandCenterUiUpdateKind.RefreshFooter];

        for (int index = 0; index < input.Length; index++)
        {
            await channel.Writer.WriteAsync(new(input[index]));

            await reader.WaitUntilIdleAfterAsync(index + 1);
        }

        channel.Writer.Complete();

        await pump.WaitAsync(AsyncTestTimeout);

        foreach (Action callback in callbacks)
        {
            callback();
        }

        Assert.Equal([CommandCenterUiUpdateKind.RefreshAll, CommandCenterUiUpdateKind.FocusSessions,
            CommandCenterUiUpdateKind.RefreshAll, CommandCenterUiUpdateKind.FocusInput,
            CommandCenterUiUpdateKind.RefreshFooter], applied);
    }

    [Fact]
    public async Task Claiming_an_older_refresh_does_not_release_a_later_pending_refresh()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        ObservedReader reader = new(channel.Reader);

        ConcurrentQueue<Action> callbacks = new();

        List<CommandCenterUiUpdateKind> applied = [];

        Task pump = CommandCenterUiUpdatePump.RunAsync(reader, callbacks.Enqueue, applied.Add,
            new TestCapturingLogger<CommandCenterUiUpdatePumpRunTests>(), CancellationToken.None);

        CommandCenterUiUpdateKind[] input = [CommandCenterUiUpdateKind.RefreshLog,
            CommandCenterUiUpdateKind.FocusSessions, CommandCenterUiUpdateKind.RefreshFooter];

        for (int index = 0; index < input.Length; index++)
        {
            await channel.Writer.WriteAsync(new(input[index]));

            await reader.WaitUntilIdleAfterAsync(index + 1);
        }

        Assert.True(callbacks.TryDequeue(out Action? first));

        first!();

        await channel.Writer.WriteAsync(new(CommandCenterUiUpdateKind.RefreshSidebar));

        await reader.WaitUntilIdleAfterAsync(4);

        channel.Writer.Complete();

        await pump.WaitAsync(AsyncTestTimeout);

        Assert.Equal(2, callbacks.Count);

        foreach (Action callback in callbacks)
        {
            callback();
        }

        Assert.Equal([CommandCenterUiUpdateKind.RefreshLog, CommandCenterUiUpdateKind.FocusSessions,
            CommandCenterUiUpdateKind.RefreshAll], applied);
    }

    [Fact]
    public async Task Refreshes_arriving_during_an_apply_form_one_following_refresh()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        ObservedReader reader = new(channel.Reader);

        ConcurrentQueue<Action> callbacks = new();

        ConcurrentQueue<CommandCenterUiUpdateKind> applied = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task pump = CommandCenterUiUpdatePump.RunAsync(reader, callbacks.Enqueue, kind =>
        {
            applied.Enqueue(kind);

            if (applied.Count == 1)
            {
                entered.SetResult();

                release.Task.WaitAsync(AsyncTestTimeout).GetAwaiter().GetResult();
            }
        }, new TestCapturingLogger<CommandCenterUiUpdatePumpRunTests>(), CancellationToken.None);

        await channel.Writer.WriteAsync(new(CommandCenterUiUpdateKind.RefreshLog));

        await reader.WaitUntilIdleAfterAsync(1);

        Assert.True(callbacks.TryDequeue(out Action? first));

        Task firstApply = Task.Run(first!);

        try
        {
            await entered.Task.WaitAsync(AsyncTestTimeout);

            for (int count = 2; count <= 31; count++)
            {
                await channel.Writer.WriteAsync(new(count % 2 == 0
                    ? CommandCenterUiUpdateKind.RefreshFooter : CommandCenterUiUpdateKind.RefreshSidebar));

                await reader.WaitUntilIdleAfterAsync(count);
            }

            channel.Writer.Complete();

            await pump.WaitAsync(AsyncTestTimeout);

            Assert.Single(callbacks);
        }
        finally
        {
            release.TrySetResult();

            await firstApply.WaitAsync(AsyncTestTimeout);

            channel.Writer.TryComplete();
        }

        Assert.Single(callbacks)();

        Assert.Equal([CommandCenterUiUpdateKind.RefreshLog, CommandCenterUiUpdateKind.RefreshAll], applied);
    }

    [Fact]
    public async Task Failed_handoff_releases_the_pending_refresh_for_later_updates()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        ObservedReader reader = new(channel.Reader);

        ConcurrentQueue<Action> callbacks = new();

        List<CommandCenterUiUpdateKind> applied = [];

        TestCapturingLogger<CommandCenterUiUpdatePumpRunTests> logger = new();

        int handoffs = 0;

        Task pump = CommandCenterUiUpdatePump.RunAsync(reader, callback =>
        {
            if (++handoffs == 1)
            {
                throw new InvalidOperationException("queue refused the callback");
            }

            callbacks.Enqueue(callback);
        }, applied.Add, logger, CancellationToken.None);

        await channel.Writer.WriteAsync(new(CommandCenterUiUpdateKind.RefreshLog));

        await reader.WaitUntilIdleAfterAsync(1);

        await channel.Writer.WriteAsync(new(CommandCenterUiUpdateKind.RefreshFooter));

        await reader.WaitUntilIdleAfterAsync(2);

        channel.Writer.Complete();

        await pump.WaitAsync(AsyncTestTimeout);

        Assert.Single(callbacks)();

        Assert.Equal([CommandCenterUiUpdateKind.RefreshFooter], applied);

        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task A_burst_of_queued_updates_is_applied_once()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        for (int i = 0; i < 5; i++)
        {
            Assert.True(channel.Writer.TryWrite(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog)));
        }

        channel.Writer.Complete();
        List<CommandCenterUiUpdateKind> applied = [];

        await CommandCenterUiUpdatePump
            .RunAsync(
                channel.Reader,
                RunInline,
                applied.Add,
                new TestCapturingLogger<CommandCenterUiUpdatePumpRunTests>(),
                CancellationToken.None)
            .WaitAsync(AsyncTestTimeout);

        Assert.Equal([CommandCenterUiUpdateKind.RefreshLog], applied);
    }

    /// <summary>
    /// One failed apply must not freeze the screen for the rest of the run, and it must not disappear: it
    /// is logged, and the next update is still applied.
    /// </summary>
    [Fact]
    public async Task An_apply_that_throws_is_logged_and_the_next_update_is_still_applied()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        TestCapturingLogger<CommandCenterUiUpdatePumpRunTests> logger = new();
        List<CommandCenterUiUpdateKind> applied = [];
        TaskCompletionSource firstApplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task pump = CommandCenterUiUpdatePump.RunAsync(
            channel.Reader,
            RunInline,
            kind =>
            {
                if (kind == CommandCenterUiUpdateKind.RefreshSidebar)
                {
                    _ = firstApplyEntered.TrySetResult();
                    throw new InvalidOperationException("layout failed");
                }

                applied.Add(kind);
                _ = secondApplied.TrySetResult();
            },
            logger,
            CancellationToken.None);

        // The second update is written only once the first apply has run, so the two are never folded.
        await channel.Writer.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshSidebar));
        await firstApplyEntered.Task.WaitAsync(AsyncTestTimeout);
        await channel.Writer.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter));

        await secondApplied.Task.WaitAsync(AsyncTestTimeout);
        channel.Writer.Complete();
        await pump.WaitAsync(AsyncTestTimeout);

        Assert.Equal([CommandCenterUiUpdateKind.RefreshFooter], applied);
        TestLogEntry failure = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    /// <summary>
    /// Production marshals each apply onto the Terminal.Gui loop, which queues it and runs it later on the
    /// UI thread, so a guard around the marshalling call never sees the apply throw. The guard must travel
    /// with the queued work: an apply that throws on the UI loop is logged there and does not escape into
    /// the loop, where it would end the whole session as a crash.
    /// </summary>
    [Fact]
    public async Task An_apply_queued_onto_the_ui_loop_that_throws_is_logged_there_and_does_not_escape()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        TestCapturingLogger<CommandCenterUiUpdatePumpRunTests> logger = new();
        List<CommandCenterUiUpdateKind> applied = [];
        List<Action> uiLoopQueue = [];
        TaskCompletionSource bothQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task pump = CommandCenterUiUpdatePump.RunAsync(
            channel.Reader,
            work =>
            {
                lock (uiLoopQueue)
                {
                    uiLoopQueue.Add(work);
                    if (uiLoopQueue.Count == 2)
                    {
                        _ = bothQueued.TrySetResult();
                    }
                }
            },
            kind =>
            {
                if (kind == CommandCenterUiUpdateKind.RefreshSidebar)
                {
                    throw new InvalidOperationException("layout failed");
                }

                applied.Add(kind);
            },
            logger,
            CancellationToken.None);

        await channel.Writer.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshSidebar));
        await channel.Writer.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.FocusInput));
        await bothQueued.Task.WaitAsync(AsyncTestTimeout);
        channel.Writer.Complete();
        await pump.WaitAsync(AsyncTestTimeout);

        Assert.Empty(logger.Entries);

        // The UI loop now runs what was queued, after the pump's own frame is long gone.
        foreach (Action work in uiLoopQueue)
        {
            work();
        }

        Assert.Equal([CommandCenterUiUpdateKind.FocusInput], applied);
        TestLogEntry failure = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.IsType<InvalidOperationException>(failure.Exception);
    }

    [Fact]
    public async Task A_cancelled_run_ends_the_pump_without_a_fault()
    {
        Channel<CommandCenterUiUpdate> channel = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        using CancellationTokenSource run = new();
        TestCapturingLogger<CommandCenterUiUpdatePumpRunTests> logger = new();

        Task pump = CommandCenterUiUpdatePump.RunAsync(
            channel.Reader,
            RunInline,
            static _ => { },
            logger,
            run.Token);
        run.Cancel();

        await pump.WaitAsync(AsyncTestTimeout);

        Assert.Empty(logger.Entries);
    }

    private static void RunInline(Action work) => work();

    private sealed class ObservedReader(ChannelReader<CommandCenterUiUpdate> inner) : ChannelReader<CommandCenterUiUpdate>
    {
        private readonly Channel<int> _idleReadCounts = Channel.CreateUnbounded<int>();

        private int _readCount;

        public override bool TryRead(out CommandCenterUiUpdate item)
        {
            if (!inner.TryRead(out item!))
            {
                return false;
            }

            _readCount++;

            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            _idleReadCounts.Writer.TryWrite(_readCount);

            return inner.WaitToReadAsync(cancellationToken);
        }

        public async Task WaitUntilIdleAfterAsync(int count)
        {
            int observed;

            do
            {
                observed = await _idleReadCounts.Reader.ReadAsync().AsTask().WaitAsync(AsyncTestTimeout);
            } while (observed < count);

            Assert.Equal(count, observed);
        }
    }
}
