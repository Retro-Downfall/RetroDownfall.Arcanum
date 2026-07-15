namespace RetroDownfall.TheForge.Ux.Services.Whispers;

public sealed class SystemWhispersClock : IWhispersClock
{

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

}
