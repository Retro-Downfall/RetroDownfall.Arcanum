namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ArcanumSettings
{

    public HostSettings Host { get; init; } = new();

    public OllamaSettings Ollama { get; init; } = new();

    public BureauSettings Bureau { get; init; } = new();

    public IntelligenceSettings Intelligence { get; init; } = new();

    public PerceptionSettings Perception { get; init; } = new();

    public CliSettings Cli { get; init; } = new();

    public SecuritySettings Security { get; init; } = new();

}


