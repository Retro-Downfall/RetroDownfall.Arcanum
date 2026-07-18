namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record PromptSettings
{

    public int MaxParameterValueChars { get; set; } = 4096;

}
