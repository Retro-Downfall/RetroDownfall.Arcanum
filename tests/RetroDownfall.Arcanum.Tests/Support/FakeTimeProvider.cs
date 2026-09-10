namespace RetroDownfall.Arcanum.Tests.Support;

public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                delta,
                "Monotonic test time cannot move backward; use SetUtcNow for wall-clock jumps.");
        }

        _now = _now.Add(delta);

        _timestamp = checked(_timestamp + delta.Ticks);
    }

    public void SetUtcNow(DateTimeOffset value)
    {
        _now = value;
    }
}
