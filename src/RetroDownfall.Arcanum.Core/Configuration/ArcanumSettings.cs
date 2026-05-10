namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ArcanumSettings
{

    public HostSettings Host { get; init; } = new();

    public ProviderSettings[] Providers { get; init; } = [];

    public string? DefaultModel { get; init; }

    public string? FastModel { get; init; }

    public BureauSettings Bureau { get; init; } = new();

    public IntelligenceSettings Intelligence { get; init; } = new();

    public PerceptionSettings Perception { get; init; } = new();

    public CliSettings Cli { get; init; } = new();

    public SecuritySettings Security { get; init; } = new();

    public DaemonSettings Daemon { get; init; } = new();

    public CommLinkSettings CommLink { get; init; } = new();

}




