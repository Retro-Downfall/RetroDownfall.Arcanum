using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterTurnLifetimeTests
{
    [Fact]
    public async Task Turn_admission_stays_closed_until_owned_source_disposal_finishes()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        using ManualResetEventSlim disposing = new();
        using ManualResetEventSlim release = new();
        GatedSource source = new(disposing, release);
        Assert.True(state.TryBeginTurn());
        state.TurnCts = source;
        Task finish = Task.Run(() => state.EndTurn(source));
        try
        {
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(state.Generating);
            Assert.False(state.TryBeginTurn());
            Assert.Null(state.TurnCts);
        }
        finally
        {
            release.Set();
            await finish.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(state.Generating);
        Assert.True(state.TryBeginTurn());
        using CancellationTokenSource next = new();
        state.TurnCts = next;
        Assert.True(state.TryCancelTurn());
        Assert.True(next.IsCancellationRequested);
        state.EndTurn(next);
    }

    [Fact]
    public void Completing_turn_never_clears_or_disposes_a_different_source()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        CancellationTokenSource owned = new();
        using CancellationTokenSource different = new();
        Assert.True(state.TryBeginTurn());
        state.TurnCts = different;
        state.EndTurn(owned);
        Assert.Same(different, state.TurnCts);
        Assert.True(state.Generating);
        Assert.True(state.TryCancelTurn());
        Assert.True(different.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => owned.Cancel());
    }

    [Fact]
    public void Missing_source_still_releases_a_claimed_turn()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Assert.True(state.TryBeginTurn());
        state.EndTurn(null);
        Assert.False(state.Generating);
        Assert.Null(state.TurnCts);
    }

    [Fact]
    public void Disposing_a_failed_source_still_releases_its_owned_turn()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        ThrowingSource source = new();
        Assert.True(state.TryBeginTurn());
        state.TurnCts = source;
        Assert.Throws<IOException>(() => state.EndTurn(source));
        Assert.Null(state.TurnCts);
        Assert.False(state.Generating);
    }

    private sealed class ThrowingSource : CancellationTokenSource
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("test source disposal failure");
        }
    }

    private sealed class GatedSource(ManualResetEventSlim disposing, ManualResetEventSlim release) : CancellationTokenSource
    {
        protected override void Dispose(bool disposingSource)
        {
            disposing.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release source disposal.");
            }
            base.Dispose(disposingSource);
        }
    }
}
