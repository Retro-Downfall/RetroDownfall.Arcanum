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

    public WardSettings Ward { get; init; } = new();

    public ApprenticeSettings Apprentices { get; init; } = new();

    public CodexSettings Codex { get; init; } = new();

    public ProvingGroundsSettings ProvingGrounds { get; init; } = new();

    public McpSettings Mcp { get; init; } = new();

    public PromptSettings Prompts { get; init; } = new();

    public ResilienceSettings Resilience { get; init; } = new();

    public MetricsSettings Metrics { get; init; } = new();

    public EmbeddingSettings Embeddings { get; init; } = new();

    public ScryingSettings Scrying { get; init; } = new();

    public ModerationsSettings Moderations { get; init; } = new();

    public StructuredOutputSettings StructuredOutput { get; init; } = new();

    public FilesSettings Files { get; init; } = new();

    public BatchesSettings Batches { get; init; } = new();

    public PricingSettings Pricing { get; init; } = new();

    public BudgetSettings Budget { get; init; } = new();

    public WebBrowsingSettings WebBrowsing { get; init; } = new();

    public ClientToolForwardingSettings ClientToolForwarding { get; init; } = new();

    public GuardrailsSettings Guardrails { get; init; } = new();

}




