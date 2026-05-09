namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record SecuritySettings
{

    public int MaxApiKeyHeaderUtf16Chars { get; init; } = 512;

}
