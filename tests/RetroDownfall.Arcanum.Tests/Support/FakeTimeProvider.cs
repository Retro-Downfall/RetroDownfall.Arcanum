namespace RetroDownfall.Arcanum.Tests.Support;

public sealed class FakeTimeProvider : TimeProvider
{

    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta)
    {

        _now = _now.Add(delta);

    }

    public void SetUtcNow(DateTimeOffset value)
    {

        _now = value;

    }

}
