using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Operator-controlled capability opt-ins. Mechanical limits and automatic behavior do not belong
/// in this section.
/// </summary>
public sealed record FeatureSettings
{

    public bool EnterpriseTelemetry { get; set; }

    public bool ScalarUi { get; set; }

    public bool Conclave { get; set; }

    public bool A2AServer { get; set; }

    public bool A2AClient { get; set; }

    public bool Apprentices { get; set; } = true;

    public bool Lexicon { get; set; } = true;

    public bool ArchiveSearch { get; set; } = true;

    public bool Metrics { get; set; } = true;

    public bool Embeddings { get; set; }

    public bool SessionSearch { get; set; }

    public bool CodebaseRetrieval { get; set; }

    public bool Saga { get; set; }

    public bool SagaExtraction { get; set; }

    public bool SemanticSpellRouting { get; set; }

    public bool Scrying { get; set; } = true;

    public bool Attachments { get; set; } = true;

    public bool ClientTools { get; set; }

    /// <summary>
    /// Enables the native web-tool family, including <c>web_search</c> and <c>read_url</c>.
    /// </summary>
    public bool WebBrowsing { get; set; }

    /// <summary>
    /// Hard gate: when false, all reasoning controls and output are disabled regardless of model or
    /// request. When true, reasoning is permitted but still only injected when an operator sets a
    /// non-null default effort or the request carries explicit reasoning options.
    /// </summary>
    public bool Reasoning { get; set; } = true;

    /// <summary>
    /// Whether client-safe reasoning output is projected to the client. When false, reasoning may
    /// still run internally for models that produce it, but nothing reaches the response.
    /// </summary>
    public bool ReasoningSummaries { get; set; }

    public bool Guardrails { get; set; }

    public bool WorkspaceChecks { get; set; } = true;

    public bool MemoryManagement { get; set; }

}

/// <summary>
/// Operator-authored integration facts, endpoints, identities, and allowlists.
/// </summary>
public sealed record IntegrationSettings
{

    public A2AIntegrationSettings A2A { get; set; } = new();

    public CommLinkIntegrationSettings CommLink { get; set; } = new();

    public EmbeddingIntegrationSettings Embeddings { get; set; } = new();

    public McpIntegrationSettings Mcp { get; set; } = new();

    public WebResearchIntegrationSettings WebResearch { get; set; } = new();

    public WorkspaceCheckIntegrationSettings WorkspaceChecks { get; set; } = new();

}

public sealed record A2AIntegrationSettings
{

    public string ServerPath { get; set; } = "/api/conclave/a2a";

    public string? AgentCardName { get; set; }

    public string? AgentCardDescription { get; set; }

    public string[] AllowedRemoteAgents { get; set; } = [];

    public string DefaultWorkspace { get; set; } = string.Empty;

}

public sealed record CommLinkIntegrationSettings
{

    /// <summary>
    /// Optional exact environment-variable name containing the secret-bearing webhook URL.
    /// When omitted, dispatch uses <c>ARCANUM_COMMLINK_WEBHOOK_URL</c>.
    /// </summary>
    public string? WebhookUrlEnvironmentVariable { get; set; }

    public string[] AllowedSchemes { get; set; } = ["https"];

    public string[] AllowedHosts { get; set; } = [];

}

public sealed record EmbeddingIntegrationSettings
{

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public int Dimensions { get; set; } = 768;

    public CodebaseIndexingIntegrationSettings CodebaseIndexing { get; set; } = new();

}

/// <summary>
/// Operator-tunable event-driven workspace indexing controls. File eligibility and traversal limits
/// remain code-owned in <see cref="CodebaseEmbeddingSettings"/>.
/// </summary>
public sealed record CodebaseIndexingIntegrationSettings
{

    /// <summary>Watcher event debounce window in milliseconds. Clamped to 50–5,000.</summary>
    public int WatcherDebounceMilliseconds { get; set; } = 300;

    /// <summary>
    /// Maximum active recursive workspace watchers. Zero disables watchers while retaining polling.
    /// Clamped to 0–128.
    /// </summary>
    public int MaxWatchers { get; set; } = 32;

    /// <summary>Periodic correctness reconciliation cadence in minutes. Clamped to 1–1,440.</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 60;

}

public sealed record McpIntegrationSettings
{

    public string[] AllowedHttpHosts { get; set; } = [];

}

public sealed record WebResearchIntegrationSettings
{
    /// <summary>Stable name of the provider used by <c>web_search</c>.</summary>
    public string SearchProvider { get; set; } = WebResearchProviderNames.Perplexity;

    /// <summary>
    /// Perplexity model used for synthesized search. Only <c>sonar</c> and <c>sonar-pro</c> are
    /// supported.
    /// </summary>
    public string PerplexityModel { get; set; } = WebResearchModels.Sonar;

    /// <summary>
    /// Optional exact environment-variable name containing the Perplexity API key. When omitted,
    /// unattended operation uses <c>ARCANUM_PERPLEXITY_API_KEY</c>.
    /// </summary>
    public string? CredentialEnvironmentVariable { get; set; }
}

public sealed record WorkspaceCheckIntegrationSettings
{

    public WorkspaceCheckExecutableCatalogSettings ExecutableCatalog { get; set; } = new();

    public Dictionary<string, WorkspaceCheckProfileSettings> CustomProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

}

/// <summary>
/// Genuine host-capacity and backpressure choices.
/// </summary>
public sealed record ExecutionSettings
{

    public int MaxConcurrentApprentices { get; set; } = 5;

    public int MaxPendingApprenticeStarts { get; set; } = 100;

    public int MaxConcurrentApprenticeBranches { get; set; } = 3;

    public int MaxConcurrentA2ATasks { get; set; } = 50;

    public int MaxSseConnections { get; set; } = 50;

    public int MaxSseConnectionsPerType { get; set; } = 20;

    public int MaxConcurrentBatches { get; set; } = 3;

    public int MaxConcurrentRequestsPerBatch { get; set; } = 1;

}

/// <summary>
/// Provider pricing and daily budget policy.
/// </summary>
public sealed record CostSettings
{

    public PricingSettings Pricing { get; set; } = new();

    public BudgetPolicySettings Budget { get; set; } = new();

}

public sealed record BudgetPolicySettings
{

    public bool Enabled { get; set; }

    public decimal DailyLimitUsd { get; set; }

}

public sealed record HostAuditPolicySettings
{

    public bool Enabled { get; set; }

    public int RetentionDays { get; set; } = 7;

    public bool RedactToolArguments { get; set; } = true;

}

public sealed record WardPolicySettings
{

    public bool Enabled { get; set; } = true;

    public List<string> ForbiddenArts { get; set; } = [];

    public bool AutoDenyInUnattendedMode { get; set; } = true;

    public bool UnattendedMode { get; set; }

}

public sealed record GuardrailsPolicySettings
{

    public bool DetectPii { get; set; } = true;

    public bool BlockToxicity { get; set; }

    public string[] ToxicityBlocklist { get; set; } = [];

    public string[] AllowedTopics { get; set; } = [];

    public string[] BlockedTopics { get; set; } = [];

    public GuardrailsAuditPolicySettings AuditLog { get; set; } = new();

}

public sealed record GuardrailsAuditPolicySettings
{

    public bool Enabled { get; set; }

    public int RetentionDays { get; set; } = 7;

}
