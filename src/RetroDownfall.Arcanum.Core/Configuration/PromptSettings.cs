namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Code-owned prompt-rendering bounds.</summary>
public sealed record PromptSettings
{

    public int MaxParameterValueChars { get; set; } = 4096;

}
