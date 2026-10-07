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
}
