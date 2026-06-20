namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ArcanumSettings
{

    public HostSettings Host { get; init; } = new();

    public ServerSettings Server { get; init; } = new();

    public ProviderSettings[] Providers { get; init; } = [];

    public string? DefaultModel { get; init; }

    public string? FastModel { get; init; }

    public ConclaveSettings Conclave { get; init; } = new();

    public IntelligenceSettings Intelligence { get; init; } = new();

    public PerceptionSettings Perception { get; init; } = new();

    public SpellSettings Spells { get; init; } = new();

    public CampaignsSettings Campaigns { get; init; } = new();

    public CliSettings Cli { get; init; } = new();

    public SecuritySettings Security { get; init; } = new();

    public DaemonSettings Daemon { get; init; } = new();

    public CommLinkSettings CommLink { get; init; } = new();

    public GrimoireSettings Grimoire { get; init; } = new();

    public EventBusSettings EventBus { get; init; } = new();

    public LogSettings Logs { get; init; } = new();

    public WorkspaceSettings Workspaces { get; init; } = new();

    public SessionSettings Sessions { get; init; } = new();

    public LlamaCppSettings LlamaCpp { get; init; } = new();

    public WardSettings Ward { get; init; } = new();

    public ApprenticeSettings Apprentices { get; init; } = new();

    public CodexSettings Codex { get; init; } = new();

    public ProvingGroundsSettings ProvingGrounds { get; init; } = new();

}




