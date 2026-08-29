using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

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

    public bool AttachmentRetrieval { get; set; }

    public bool Saga { get; set; }

    public bool SagaExtraction { get; set; }

    /// <summary>
    /// The Tapestry — hierarchical (RAPTOR-style) summary trees woven over The Weave's existing
    /// chunk corpora. Derives the embedding substrate; defaults off.
    /// </summary>
    public bool Tapestry { get; set; }

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

    /// <summary>
    /// Whether The Covenant participates in turns. Default <c>false</c>, and the default is the
    /// contract: an installation that never names this key adds no Covenant prompt bytes, performs
    /// no Covenant read, and produces byte-identical prompts to a build without the feature.
    /// </summary>
    /// <remarks>
    /// Enabling it sends eligible content on every provider attempt, so the operator sees
    /// <see cref="RetroDownfall.Arcanum.Core.Covenant.CovenantExternalRetentionDisclosure.EnablementText"/>
    /// and the resolved provider-retention help targets before the toggle is interactive
    /// (§10.18).
    /// </remarks>
    public bool Covenant { get; set; }

    /// <summary>
    /// Whether cross-session memory follows the work rather than the installation. Default
    /// <c>false</c>, and the default is the contract: with it unset, Saga retrieval and Lexicon matching
    /// see exactly the candidate sets and the ordering they see today.
    /// </summary>
    /// <remarks>
    /// Turning it on <i>narrows</i> what the model can recall, which is the point: a conclusion drawn
    /// inside one Campaign stops competing for a bounded top-K against the Campaign in front of the
    /// model, and stops contradicting it. Widening it again is a configuration change and not a data
    /// change — nothing is deleted, re-scoped, or re-embedded either way.
    ///
    /// <para>A <c>{ get; set; }</c> property, like every other key in this record: the configuration
    /// binding generator silently skips <c>init</c>-only properties (dotnet/runtime#107856), which would
    /// leave the feature permanently off while <c>arcanum.json</c> said otherwise.</para>
    /// </remarks>
    public bool CampaignScopedMemory { get; set; }

    /// <summary>
    /// Whether durable memory records what it claimed, when that was true, and when Arcanum came to hold
    /// it. Default <c>false</c>, and the default is the contract: with it unset nothing is appended, and
    /// every store behaves exactly as it does today.
    /// </summary>
    /// <remarks>
    /// The schema installs either way, because schema evolution is not optional, and the upgrade sweep
    /// that claims an installation's existing memories runs with it. What this governs is whether a
    /// <i>new</i> write appends a claim.
    ///
    /// <para>A memory written while this is off therefore carries no claim and receives none
    /// retroactively — the sweep runs once, when the version step runs, and nothing re-runs it. An
    /// unclaimed durable row is a first-class state rather than an error.</para>
    ///
    /// <para>Erasure is deliberately not gated. A claim written while this was on stays removable after
    /// it is turned off, or disabling the feature would strand records no surface can reach and no reset
    /// can clear.</para>
    ///
    /// <para>A <c>{ get; set; }</c> property, like every other key in this record: the configuration
    /// binding generator silently skips <c>init</c>-only properties (dotnet/runtime#107856), which would
    /// leave the feature permanently off while <c>arcanum.json</c> said otherwise.</para>
    /// </remarks>
    public bool Annals { get; set; }

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

    /// <summary>
    /// Optional environment-variable name holding the credential the Archmage Client presents to remote
    /// agents. Empty (default) sends no credential. The value itself never appears in configuration.
    /// </summary>
    public string OutboundCredentialEnvironmentVariable { get; set; } = string.Empty;

    /// <summary>
    /// Header the outbound credential is sent in. Defaults to Arcanum's own API-key header so one Arcanum
    /// can reach another; set it to <c>Authorization</c> (with a <c>Bearer …</c> value) for other agents.
    /// </summary>
    public string OutboundCredentialHeader { get; set; } = "X-Arcanum-Key";

    /// <summary>
    /// Skills advertised on the Agent Card ("Heraldry"). Empty (default) advertises the single
    /// <c>apprentice-goal-execution</c> skill, so an operator who declares nothing serves exactly the
    /// card Arcanum served before this setting existed.
    /// </summary>
    public A2ASkillSettings[] Skills { get; set; } = [];

    /// <summary>
    /// Media types inbound Sendings may carry. Empty (default) advertises <c>text/plain</c>.
    /// </summary>
    public string[] InputModes { get; set; } = [];

    /// <summary>
    /// Media types this instance can answer with. Empty (default) advertises <c>text/plain</c>. A peer
    /// that accepts none of these is refused by name rather than silently answered as text.
    /// </summary>
    public string[] OutputModes { get; set; } = [];

    /// <summary>
    /// Enables the A2A push-notification surface: inbound, peers may register a callback URL and receive
    /// task-state transitions; outbound, a Sending may be dispatched in callback mode so it stops holding
    /// a concurrency slot while the remote works. Default <c>false</c>, and the Agent Card advertises
    /// <c>pushNotifications</c> only when it is on (issue #67).
    /// </summary>
    public bool PushNotifications { get; set; }

    /// <summary>
    /// Externally reachable base URL peers should post outbound-Sending callbacks to, e.g.
    /// <c>https://arcanum.example.com</c>. Empty (default) means callback mode is unavailable: this
    /// instance has no way to tell a peer where to reach it. The callback path itself is derived from
    /// <see cref="ServerPath"/>.
    /// </summary>
    public string PushCallbackBaseUrl { get; set; } = string.Empty;

}

/// <summary>
/// One operator-declared A2A skill on the Agent Card.
/// </summary>
/// <remarks>
/// Advertised capability only: declaring a skill describes what this Arcanum is for, it does not create
/// or restrict behavior. Modalities left empty inherit the card-level
/// <see cref="A2AIntegrationSettings.InputModes"/> / <see cref="A2AIntegrationSettings.OutputModes"/>.
/// </remarks>
public sealed record A2ASkillSettings
{

    /// <summary>Stable identifier peers match on. Required; a skill without one is ignored.</summary>
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string[] InputModes { get; set; } = [];

    public string[] OutputModes { get; set; } = [];

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

    /// <summary>
    /// Operator policy for The Tapestry (<c>Arcanum:Features:Tapestry</c>). Tree-shaping bounds stay
    /// code-owned mechanics — see <see cref="TapestryEmbeddingSettings"/>.
    /// </summary>
    public TapestryIntegrationSettings Tapestry { get; set; } = new();

}

/// <summary>
/// The Tapestry's operator-facing policy: which retrieval shape to use, and which model pays for
/// cluster summaries.
/// </summary>
public sealed record TapestryIntegrationSettings
{

    /// <summary>
    /// <c>CollapsedTree</c> (default) searches leaf and summary nodes as one pool;
    /// <c>TreeTraversal</c> descends level by level from the terminal layer.
    /// </summary>
    public TapestryRetrievalMode RetrievalMode { get; set; } = TapestryRetrievalMode.CollapsedTree;

    /// <summary>
    /// Model used for cluster summaries. Blank falls back to <c>Arcanum:FastModel</c>, then
    /// <c>Arcanum:DefaultModel</c>.
    /// </summary>
    public string? SummaryModel { get; set; }

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

    public bool RedactToolArguments { get; set; } = true;

}

public sealed record WardPolicySettings
{

    public bool Enabled { get; set; } = true;

    public List<string> ForbiddenArts { get; set; } = [];

    public bool AutoDenyInUnattendedMode { get; set; } = true;

    public bool UnattendedMode { get; set; }

    public WardAutoApprovePolicySettings AutoApprove { get; set; } = new();

}

/// <summary>
/// Narrow, opt-in operator consent given in advance for named Ward-gated tools. This substitutes for
/// the human approval step only: Sanctum, <c>WorkspacePathPolicy</c>, edition and host-process gates,
/// Artifact Attunement, and <c>workspace_check</c> eligibility are unconditional and still run after
/// an auto-approval. A hard denial always wins over a matching entry here.
/// </summary>
public sealed record WardAutoApprovePolicySettings
{

    /// <summary>Master opt-in. Default <c>false</c>; an empty <see cref="Tools"/> list is a no-op.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Exact tool names (matched ordinal-ignore-case, as elsewhere in the tool registry) the host may
    /// auto-approve. Blank and duplicate entries are rejected at startup.
    /// </summary>
    public List<string> Tools { get; set; } = [];

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

}
