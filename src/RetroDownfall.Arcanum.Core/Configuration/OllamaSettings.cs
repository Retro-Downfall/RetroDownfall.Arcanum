namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class OllamaSettings
{
    public string Endpoint { get; init; } = "http://localhost:11434";

    public string DefaultModel { get; init; } = "llama3.2";
}
