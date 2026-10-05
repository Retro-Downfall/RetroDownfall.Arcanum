using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterStateTests
{
    /// <summary>
    /// Ctrl+C reaches the turn's token source from a fire-and-forget task, while the submit path's
    /// finally disposes it. Cancelling or reading the token of a disposed source throws, and in that task
    /// nothing observes the exception.
    /// </summary>
    [Fact]
    public void TryCancelTurn_after_dispose_is_a_noop()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        CancellationTokenSource turn = new();
        state.TurnCts = turn;
        turn.Dispose();

        // The hazard this guards: the framework itself throws once the source is disposed.
        _ = Assert.Throws<ObjectDisposedException>(() => turn.Cancel());

        Assert.False(state.TryCancelTurn());
    }

    [Fact]
    public void TryCancelTurn_cancels_a_live_turn()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        using CancellationTokenSource turn = new();
        state.TurnCts = turn;

        Assert.True(state.TryCancelTurn());
        Assert.True(turn.IsCancellationRequested);
    }

    [Fact]
    public void TryCancelTurn_without_a_turn_is_a_noop()
    {
        CommandCenterState state = new(new SessionLogBuffer());

        Assert.False(state.TryCancelTurn());
    }

    [Fact]
    public void TurnTokenOr_hands_back_the_fallback_when_the_turn_source_is_gone_or_disposed()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        using CancellationTokenSource fallbackSource = new();
        CancellationToken fallback = fallbackSource.Token;

        Assert.Equal(fallback, state.TurnTokenOr(fallback));

        CancellationTokenSource turn = new();
        state.TurnCts = turn;
        Assert.Equal(turn.Token, state.TurnTokenOr(fallback));

        turn.Dispose();
        Assert.Equal(fallback, state.TurnTokenOr(fallback));
    }

    /// <summary>
    /// Race the two sides for real: cancellations landing while the owner swaps and disposes sources
    /// must never throw out of <see cref="CommandCenterState.TryCancelTurn"/>.
    /// </summary>
    [Fact]
    public async Task TryCancelTurn_never_throws_while_the_owner_disposes_sources()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(1));
        List<Exception> failures = [];

        Task canceller = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _ = state.TryCancelTurn();
                    _ = state.TurnTokenOr(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            }
        });

        Task owner = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                CancellationTokenSource turn = new();
                state.TurnCts = turn;
                CancellationTokenSource? captured = state.TurnCts;
                state.TurnCts = null;
                captured?.Dispose();
            }
        });

        await Task.WhenAll(canceller, owner).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(failures.Count == 0, $"{failures.Count} failures; first: {failures.FirstOrDefault()}");
    }
}
