using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// Deterministic stand-in for the Avalonia dispatcher. <see cref="OnUiThread"/> decides which branch
/// of a <c>CheckAccess</c>/<c>Post</c> guard a subject takes, and posted work only runs when the test
/// drains it — so "was this marshalled?" is an assertion rather than a race.
/// </summary>
internal sealed class RecordingUiThreadDispatcher : IUiThreadDispatcher
{

    public bool OnUiThread { get; set; }

    public List<Action> Pending { get; } = [];

    public bool CheckAccess() => OnUiThread;

    public void Post(Action action) => Pending.Add(action);

    public void DrainPending()
    {

        Action[] queued = [.. Pending];

        Pending.Clear();

        foreach (Action action in queued)
        {

            action();

        }

    }

}
