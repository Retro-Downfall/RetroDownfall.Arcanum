namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ThemeColors
{

    public ThemeSemanticColors Light { get; init; } = new();

    public ThemeSemanticColors Dark { get; init; } = new()
    {

        Text = "#E8DCC4",

        Heading = "#00FFD5",

        Highlight = "#39FF14",

        Error = "#FF6B6B",

        Muted = "#7A6B90",

    };

}
