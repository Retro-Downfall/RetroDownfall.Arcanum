namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ArcanumSettings
{

    public HostSettings Host { get; set; } = new();

    public ServerSettings Server { get; set; } = new();

    public ProviderSettings[] Providers { get; set; } = [];

    public string? DefaultModel { get; set; }

    public string? FastModel { get; set; }

    public ConclaveSettings Conclave { get; set; } = new();

    public IntelligenceSettings Intelligence { get; set; } = new();

    public PerceptionSettings Perception { get; set; } = new();

    public SpellSettings Spells { get; set; } = new();

    public CampaignsSettings Campaigns { get; set; } = new();

    public CliSettings Cli { get; set; } = new();

    public SecuritySettings Security { get; set; } = new();

    public DaemonSettings Daemon { get; set; } = new();

    public CommLinkSettings CommLink { get; set; } = new();

    public GrimoireSettings Grimoire { get; set; } = new();

    public EventBusSettings EventBus { get; set; } = new();

    public LogSettings Logs { get; set; } = new();

    public WorkspaceSettings Workspaces { get; set; } = new();

    public SessionSettings Sessions { get; set; } = new();

    public WardSettings Ward { get; set; } = new();

    public ApprenticeSettings Apprentices { get; set; } = new();

    public CodexSettings Codex { get; set; } = new();

    public ProvingGroundsSettings ProvingGrounds { get; set; } = new();

    public McpSettings Mcp { get; set; } = new();

    public PromptSettings Prompts { get; set; } = new();

    public ResilienceSettings Resilience { get; set; } = new();

    public MetricsSettings Metrics { get; set; } = new();

    public EmbeddingSettings Embeddings { get; set; } = new();

    public ScryingSettings Scrying { get; set; } = new();

    public ModerationsSettings Moderations { get; set; } = new();

    public StructuredOutputSettings StructuredOutput { get; set; } = new();

    public FilesSettings Files { get; set; } = new();

    public BatchesSettings Batches { get; set; } = new();

    public PricingSettings Pricing { get; set; } = new();

    public BudgetSettings Budget { get; set; } = new();

    public WebBrowsingSettings WebBrowsing { get; set; } = new();

    public ClientToolForwardingSettings ClientToolForwarding { get; set; } = new();

    public GuardrailsSettings Guardrails { get; set; } = new();

}




