using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class GrimoireDbReadiness : IGrimoireDbReadiness
{

    private volatile bool _isReady;

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _isReady;

    public void MarkReady()
    {
        _isReady = true;
        _ = _ready.TrySetResult();
    }

    public void MarkFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _ = _ready.TrySetException(exception);
    }

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        _ready.Task.WaitAsync(cancellationToken);

}
