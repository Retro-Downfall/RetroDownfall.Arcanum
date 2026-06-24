namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record PromptSettings
{

    public int MaxParameterValueChars { get; init; } = 4096;

}
