namespace RetroDownfall.Arcanum.Cli.UX;

internal sealed class StreamingRenderCadence
{
    public const int DefaultChunkThreshold = 32;

    public const long DefaultIntervalMilliseconds = 75;

    private readonly Func<long> _elapsedMilliseconds;
    private readonly int _chunkThreshold;
    private readonly long _intervalMilliseconds;
    private int _chunksSinceRefresh;
    private long _lastRefreshMilliseconds;

    public StreamingRenderCadence(
        Func<long> elapsedMilliseconds,
        int chunkThreshold = DefaultChunkThreshold,
        long intervalMilliseconds = DefaultIntervalMilliseconds)
    {
        _elapsedMilliseconds = elapsedMilliseconds
            ?? throw new ArgumentNullException(nameof(elapsedMilliseconds));
        _chunkThreshold = Math.Max(1, chunkThreshold);
        _intervalMilliseconds = Math.Max(1, intervalMilliseconds);
        _lastRefreshMilliseconds = _elapsedMilliseconds();
    }

    public void NoteChunk() => _chunksSinceRefresh++;

    public bool ShouldRefresh(bool force) =>
        force
        || _chunksSinceRefresh >= _chunkThreshold
        || _elapsedMilliseconds() - _lastRefreshMilliseconds >= _intervalMilliseconds;

    public void MarkRefreshed()
    {
        _chunksSinceRefresh = 0;
        _lastRefreshMilliseconds = _elapsedMilliseconds();
    }
}
