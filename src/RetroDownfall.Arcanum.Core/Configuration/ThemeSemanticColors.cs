namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ThemeSemanticColors
{

    public string Text { get; init; } = "#2A1545";

    public string Heading { get; init; } = "#1E3A8A";

    public string Highlight { get; init; } = "#008F11";

    public string Error { get; init; } = "#C41E3A";

    public string Muted { get; init; } = "#6B5D7A";

}
