namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ArcanumSettings
{

    public OllamaSettings Ollama { get; init; } = new();

    public BureauSettings Bureau { get; init; } = new();

}
