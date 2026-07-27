using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Compendium.Ux.Models;

/// <summary>
/// Editable public configuration choices. Schema-only implementation descriptors are deliberately
/// excluded: every row in this table is rendered by either a polished page or the generic editor.
/// </summary>
public static class SettingDescriptors
{

    public static IReadOnlyList<SettingDescriptor> All { get; } =
    [
        // Edition
        new("edition", ConfigSection.Edition, "Edition", "Runtime hardening mode. ARCANUM_EDITION may override this value.", SettingKind.Enum, EnumType: typeof(ArcanumEdition)),

        // Host
        new("host.port", ConfigSection.Host, "Port", "Kestrel HTTP listen port.", SettingKind.Int, 1, 65_535, 1, ClampName: nameof(ArcanumSettingClamps.HostPort)),
        new("host.corsAllowedOrigins", ConfigSection.Host, "CORS allowed origins", "Browser origins permitted to read Arcanum responses.", SettingKind.StringArray),
        new("host.listenAny", ConfigSection.Host, "Listen on all interfaces", "Bind externally using HTTPS-only. External binding also enforces rate limiting and metrics authentication.", SettingKind.Bool),
        new("host.auditLog.enabled", ConfigSection.Host, "Inference audit log", "Persist an append-only audit trail for completed inference turns.", SettingKind.Bool, Group: "Audit policy"),
        new("host.auditLog.retentionDays", ConfigSection.Host, "Audit retention (days)", "Delete dated inference audit files older than this compliance retention period.", SettingKind.Int, 1, 365, 1, ClampName: nameof(ArcanumSettingClamps.HostAuditLogRetentionDays), Group: "Audit policy"),
        new("host.auditLog.redactToolArguments", ConfigSection.Host, "Redact tool arguments", "Store tool names without potentially sensitive argument JSON.", SettingKind.Bool, Group: "Audit policy"),
        new("host.https.enabled", ConfigSection.Host, "Enable HTTPS", "Add the TLS listener; required for all-interface binding.", SettingKind.Bool, Group: "HTTPS"),
        new("host.https.port", ConfigSection.Host, "HTTPS port", "TLS listen port. It must differ from the HTTP port.", SettingKind.Int, 1, 65_535, 1, ClampName: nameof(ArcanumSettingClamps.HostHttpsPort), Group: "HTTPS"),
        new("host.https.certificatePath", ConfigSection.Host, "Certificate path", "PFX bundle path, or PEM certificate path when a private key path is supplied.", SettingKind.Path, Placeholder: "~/.config/arcanum/certs/localhost.pfx", Group: "HTTPS"),
        new("host.https.privateKeyPath", ConfigSection.Host, "Private key path", "Optional PEM private key path.", SettingKind.Path, Placeholder: "~/.config/arcanum/certs/localhost.key", Group: "HTTPS"),
        new("host.https.certificatePasswordEnvironmentVariable", ConfigSection.Host, "Certificate password environment variable", "Exact variable containing the PFX password. Leave blank to use ARCANUM_HTTPS_CERTIFICATE_PASSWORD; ignored for PEM.", SettingKind.String, Placeholder: "ARCANUM_HTTPS_CERTIFICATE_PASSWORD", Group: "HTTPS"),
        new("host.minLogLevelInBuffer", ConfigSection.Host, "Buffered log level", "Minimum log level retained in the in-memory diagnostics buffer.", SettingKind.Enum, EnumType: typeof(LogLevel), Group: "Observability"),

        // Providers and top-level model selections
        new("defaultModel", ConfigSection.Providers, "Default model", "Model used when a request does not select one.", SettingKind.String, Placeholder: "gpt-4o"),
        new("fastModel", ConfigSection.Providers, "Fast model", "Optional model used automatically for eligible internal work.", SettingKind.String, Placeholder: "gpt-4o-mini"),
        new("providers.name", ConfigSection.Providers, "Provider name", "Human-readable provider identity.", SettingKind.String),
        new("providers.type", ConfigSection.Providers, "Provider type", "Provider wire contract.", SettingKind.Enum, EnumType: typeof(AiProviderKind)),
        new("providers.endpoint", ConfigSection.Providers, "Endpoint", "OpenAI-compatible base endpoint.", SettingKind.String, Placeholder: "https://api.openai.com/v1"),
        new("providers.credentialEnvironmentVariable", ConfigSection.Providers, "Credential environment variable", "Exact variable containing the provider API key. Leave blank to derive ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY.", SettingKind.String, Placeholder: "OPENAI_API_KEY"),
        new("providers.models.name", ConfigSection.Providers, "Model name", "Model ID advertised by this provider.", SettingKind.String),
        new("providers.models.supportsVision", ConfigSection.Providers, "Supports vision", "The model accepts image content.", SettingKind.Bool),
        new("providers.models.reasoning.controlSupport", ConfigSection.Providers, "Reasoning controls", "Reasoning controls accepted by this model.", SettingKind.Enum, EnumType: typeof(ReasoningControlSupport), Group: "Reasoning capability"),
        new("providers.models.reasoning.supportsSummary", ConfigSection.Providers, "Supports reasoning summaries", "The model can return a client-safe reasoning summary.", SettingKind.Bool, Group: "Reasoning capability"),
        new("providers.models.reasoning.supportsFull", ConfigSection.Providers, "Supports full reasoning", "The provider can return explicitly client-safe full reasoning output.", SettingKind.Bool, Group: "Reasoning capability"),
        new("providers.models.reasoning.supportsStreaming", ConfigSection.Providers, "Streams reasoning", "Client-safe reasoning output may arrive incrementally.", SettingKind.Bool, Group: "Reasoning capability"),
        new("providers.models.reasoning.reportsReasoningTokens", ConfigSection.Providers, "Reports reasoning tokens", "Usage reports reasoning tokens as a completion-token subset.", SettingKind.Bool, Group: "Reasoning capability"),
        new("providers.models.reasoning.allowsClientOutput", ConfigSection.Providers, "Allow client output", "Permit projection of provider-returned client-safe reasoning.", SettingKind.Bool, Group: "Reasoning capability"),
        new("providers.models.reasoning.wireDialect", ConfigSection.Providers, "Reasoning wire dialect", "Exact request wire shape required by this provider/model.", SettingKind.Enum, EnumType: typeof(ReasoningWireDialect), Group: "Reasoning capability"),
        new("providers.models.reasoning.maxBudgetTokens", ConfigSection.Providers, "Maximum reasoning budget", "Optional maximum supported numeric reasoning budget.", SettingKind.Int, 1, 2_097_152, 1, EnumType: null, ClampName: nameof(ArcanumSettingClamps.ReasoningBudgetTokens), Group: "Reasoning capability", AllowUnset: true),
        new("providers.contextWindowLimit", ConfigSection.Providers, "Context window limit", "Factual provider context-window capacity.", SettingKind.Int, 256, 2_097_152, 128, ClampName: nameof(ArcanumSettingClamps.ContextWindowLimit)),

        // Security policy
        new("security.allowUnsandboxedToolChildren", ConfigSection.Security, "Allow unsandboxed tool children", "Explicitly permit process tools without an OS filesystem jail where that escape hatch is supported.", SettingKind.Bool),
        new("security.metricsRequireApiKey", ConfigSection.Security, "Require API key for metrics", "Require authentication for loopback metrics scrapes; external binding always forces authentication.", SettingKind.Bool),
        new("security.ward.enabled", ConfigSection.Security, "Wards enabled", "Enable Forbidden Arts approval policy. Intrinsic Ward tools remain code-owned.", SettingKind.Bool, Group: "Ward"),
        new("security.ward.forbiddenArts", ConfigSection.Security, "Additional forbidden arts", "Operator additions to the intrinsic Ward tool set.", SettingKind.StringArray, Group: "Ward"),
        new("security.ward.autoDenyInUnattendedMode", ConfigSection.Security, "Auto-deny unattended requests", "Deny Ward-gated tools automatically for unattended execution.", SettingKind.Bool, Group: "Ward"),
        new("security.ward.unattendedMode", ConfigSection.Security, "Default unattended mode", "Default policy for operator-facing chat. Daemons remain unattended.", SettingKind.Bool, Group: "Ward"),
        new("security.guardrails.detectPii", ConfigSection.Security, "Detect PII", "Reject configured PII patterns before inference.", SettingKind.Bool, Group: "Guardrails"),
        new("security.guardrails.blockToxicity", ConfigSection.Security, "Block toxicity", "Apply the authored toxicity blocklist.", SettingKind.Bool, Group: "Guardrails"),
        new("security.guardrails.toxicityBlocklist", ConfigSection.Security, "Toxicity blocklist", "Case-insensitive authored toxicity terms.", SettingKind.StringArray, Group: "Guardrails"),
        new("security.guardrails.allowedTopics", ConfigSection.Security, "Allowed topics", "Optional allowlist of topic patterns.", SettingKind.StringArray, Group: "Guardrails"),
        new("security.guardrails.blockedTopics", ConfigSection.Security, "Blocked topics", "Optional blocklist of topic patterns.", SettingKind.StringArray, Group: "Guardrails"),
        new("security.guardrails.auditLog.enabled", ConfigSection.Security, "Guardrails audit log", "Persist guardrail violation records.", SettingKind.Bool, Group: "Guardrails audit"),
        new("security.guardrails.auditLog.retentionDays", ConfigSection.Security, "Guardrails audit retention (days)", "Delete dated guardrail audit files older than this compliance retention period.", SettingKind.Int, 1, 365, 1, ClampName: nameof(ArcanumSettingClamps.HostAuditLogRetentionDays), Group: "Guardrails audit"),
        new("security.perceptionWorkspaceRoots", ConfigSection.Security, "Perception roots", "Absolute roots that Perception may scan.", SettingKind.StringArray, Group: "Path authority"),
        new("security.spellWorkspaceRoots", ConfigSection.Security, "Spell roots", "Absolute roots that spell CRUD may access.", SettingKind.StringArray, Group: "Path authority"),
        new("security.campaignRoots", ConfigSection.Security, "Campaign roots", "Absolute roots from which campaigns may be registered.", SettingKind.StringArray, Group: "Path authority"),
        new("security.allowedUploadMimeTypes", ConfigSection.Security, "Upload MIME types", "Allowed OpenAI-compatible file upload MIME types; empty means no additional operator restriction.", SettingKind.StringArray, Group: "Content policy"),
        new("security.allowedImageMimeTypes", ConfigSection.Security, "Image MIME types", "Allowed Scrying image MIME types.", SettingKind.StringArray, Group: "Content policy"),

        // Workspaces
        new("workspaces.defaultRoot", ConfigSection.Workspaces, "Default workspace root", "Default workspace used by workspace-scoped routes.", SettingKind.Path, Placeholder: "/home/me/projects"),
        new("workspaces.enableFileWrite", ConfigSection.Workspaces, "Enable workspace writes", "Permit workspace file create, modify, and delete routes.", SettingKind.Bool),

        // Feature opt-ins
        new("features.enterpriseTelemetry", ConfigSection.Features, "Enterprise telemetry", "Emit structured enterprise telemetry.", SettingKind.Bool),
        new("features.scalarUi", ConfigSection.Features, "Scalar API UI", "Mount the interactive Scalar API documentation UI.", SettingKind.Bool),
        new("features.conclave", ConfigSection.Features, "Conclave", "Enable cross-Apprentice delegation.", SettingKind.Bool),
        new("features.a2AServer", ConfigSection.Features, "A2A server", "Expose configured A2A server endpoints.", SettingKind.Bool),
        new("features.a2AClient", ConfigSection.Features, "A2A client", "Enable dispatch to allowed remote A2A agents.", SettingKind.Bool),
        new("features.apprentices", ConfigSection.Features, "Apprentices", "Enable the Apprentice subsystem.", SettingKind.Bool),
        new("features.lexicon", ConfigSection.Features, "Lexicon", "Enable model-writable Lexicon memory.", SettingKind.Bool),
        new("features.archiveSearch", ConfigSection.Features, "Archive search", "Enable search over past sessions.", SettingKind.Bool),
        new("features.metrics", ConfigSection.Features, "Metrics", "Expose the Prometheus metrics endpoint.", SettingKind.Bool),
        new("features.embeddings", ConfigSection.Features, "Embeddings", "Enable The Weave embedding substrate.", SettingKind.Bool),
        new("features.sessionSearch", ConfigSection.Features, "Session search", "Enable semantic search over sessions.", SettingKind.Bool),
        new("features.codebaseRetrieval", ConfigSection.Features, "Codebase retrieval", "Enable semantic workspace retrieval.", SettingKind.Bool),
        new("features.saga", ConfigSection.Features, "Saga", "Enable long-term associative memory retrieval.", SettingKind.Bool),
        new("features.sagaExtraction", ConfigSection.Features, "Saga extraction", "Allow automatic extraction of new Saga memories.", SettingKind.Bool),
        new("features.semanticSpellRouting", ConfigSection.Features, "Semantic spell routing", "Enable embedding-assisted spell routing.", SettingKind.Bool),
        new("features.scrying", ConfigSection.Features, "Scrying", "Accept image content for vision-capable models.", SettingKind.Bool),
        new("features.attachments", ConfigSection.Features, "Attachments", "Persist session attachments and expose the session attachment tool.", SettingKind.Bool),
        new("features.clientTools", ConfigSection.Features, "Client tools", "Forward client-declared tools to compatible providers.", SettingKind.Bool),
        new("features.webBrowsing", ConfigSection.Features, "Web browsing", "Advertise the guarded browse_web tool.", SettingKind.Bool),
        new("features.guardrails", ConfigSection.Features, "Guardrails", "Run configured input and output guardrails.", SettingKind.Bool),
        new("features.workspaceChecks", ConfigSection.Features, "Workspace checks", "Allow workspace_check advertisement when all security eligibility requirements pass.", SettingKind.Bool),
        new("features.memoryManagement", ConfigSection.Features, "Memory management", "Enable session entry deletion, pinning, and compaction.", SettingKind.Bool),

        // Integration facts and allowlists
        new("integrations.a2A.serverPath", ConfigSection.Integrations, "A2A server path", "Path used for A2A endpoints and Agent Card discovery.", SettingKind.String, Placeholder: "/api/conclave/a2a", Group: "A2A"),
        new("integrations.a2A.agentCardName", ConfigSection.Integrations, "Agent Card name", "Advertised A2A identity name.", SettingKind.String, Group: "A2A"),
        new("integrations.a2A.agentCardDescription", ConfigSection.Integrations, "Agent Card description", "Advertised A2A identity description.", SettingKind.String, Group: "A2A"),
        new("integrations.a2A.allowedRemoteAgents", ConfigSection.Integrations, "Allowed remote agents", "Allowed remote Agent Card URLs or origins.", SettingKind.StringArray, Group: "A2A"),
        new("integrations.a2A.defaultWorkspace", ConfigSection.Integrations, "A2A default workspace", "Fallback workspace for inbound A2A tasks.", SettingKind.Path, Group: "A2A"),
        new("integrations.commLink.webhookUrlEnvironmentVariable", ConfigSection.Integrations, "CommLink webhook environment variable", "Environment-variable reference containing the secret-bearing webhook URL. Defaults to ARCANUM_COMMLINK_WEBHOOK_URL.", SettingKind.String, Placeholder: "ARCANUM_COMMLINK_WEBHOOK_URL", Group: "CommLink"),
        new("integrations.commLink.allowedSchemes", ConfigSection.Integrations, "CommLink schemes", "Allowed webhook URI schemes.", SettingKind.StringArray, Group: "CommLink"),
        new("integrations.commLink.allowedHosts", ConfigSection.Integrations, "CommLink hosts", "Optional webhook host allowlist.", SettingKind.StringArray, Group: "CommLink"),
        new("integrations.embeddings.provider", ConfigSection.Integrations, "Embedding provider", "Configured provider used to generate embeddings.", SettingKind.String, Group: "Embeddings"),
        new("integrations.embeddings.model", ConfigSection.Integrations, "Embedding model", "Embedding model advertised by the selected provider.", SettingKind.String, Group: "Embeddings"),
        new("integrations.embeddings.dimensions", ConfigSection.Integrations, "Embedding dimensions", "Expected vector dimensions. Changing this requires re-indexing or reinstalling the local database.", SettingKind.Int, 64, 4_096, 8, ClampName: nameof(ArcanumSettingClamps.EmbeddingsDimensions), Group: "Embeddings"),
        new("integrations.mcp.allowedHttpHosts", ConfigSection.Integrations, "MCP plaintext hosts", "Hosts explicitly allowed to use plaintext HTTP MCP transport.", SettingKind.StringArray, Group: "MCP"),
        new("integrations.workspaceChecks.executableCatalog.dotNet.path", ConfigSection.Integrations, "Trusted dotnet executable", "Optional canonical absolute path to a trusted native dotnet executable.", SettingKind.Path, Group: "Workspace checks"),
        new("integrations.workspaceChecks.customProfiles", ConfigSection.Integrations, "Custom workspace-check profiles", "Source-generated JSON editor for operator-authored closed profile definitions.", SettingKind.Dictionary, Group: "Workspace checks"),

        // Host capacity and backpressure
        new("execution.maxConcurrentApprentices", ConfigSection.Execution, "Concurrent Apprentices", "Maximum Apprentices the host may run simultaneously.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.MaxConcurrentApprentices)),
        new("execution.maxPendingApprenticeStarts", ConfigSection.Execution, "Pending Apprentice starts", "Backpressure queue for Apprentice start requests.", SettingKind.Int, 1, 1_000, 1, ClampName: nameof(ArcanumSettingClamps.MaxPendingStarts)),
        new("execution.maxConcurrentApprenticeBranches", ConfigSection.Execution, "Concurrent Apprentice branches", "Maximum Simulacrum branches the host may run simultaneously within an Apprentice.", SettingKind.Int, 1, 64, 1, ClampName: nameof(ArcanumSettingClamps.MaxConcurrentApprenticeBranches)),
        new("execution.maxConcurrentA2ATasks", ConfigSection.Execution, "Concurrent A2A tasks", "Maximum external A2A delegations the host may keep in flight.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxConcurrentA2ATasks)),
        new("execution.maxSseConnections", ConfigSection.Execution, "SSE connections", "Global live-event connection capacity.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxSseConnections)),
        new("execution.maxSseConnectionsPerType", ConfigSection.Execution, "SSE connections per type", "Per-stream-family fairness capacity.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.SseConnectionsPerType)),
        new("execution.maxConcurrentBatches", ConfigSection.Execution, "Concurrent batches", "Maximum OpenAI-compatible batches processed concurrently.", SettingKind.Int, 1, 20, 1, ClampName: nameof(ArcanumSettingClamps.BatchesMaxConcurrentBatches)),
        new("execution.maxConcurrentRequestsPerBatch", ConfigSection.Execution, "Concurrent requests per batch", "Per-batch request concurrency.", SettingKind.Int, 1, 10, 1, ClampName: nameof(ArcanumSettingClamps.BatchesMaxConcurrentRequestsPerBatch)),

        // Cost
        new("cost.pricing.defaultPricing.inputPer1M", ConfigSection.Cost, "Default input price", "Fallback USD per one million input tokens.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingInputPer1M), Group: "Default pricing"),
        new("cost.pricing.defaultPricing.outputPer1M", ConfigSection.Cost, "Default output price", "Fallback USD per one million output tokens.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingOutputPer1M), Group: "Default pricing"),
        new("cost.pricing.defaultPricing.reasoningPer1M", ConfigSection.Cost, "Default reasoning price", "Optional USD per one million reasoning tokens; unset uses output pricing.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingOutputPer1M), Group: "Default pricing", AllowUnset: true),
        new("cost.pricing.defaultPricing.cachedPer1M", ConfigSection.Cost, "Default cached-input price", "Fallback USD per one million cached input tokens.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingInputPer1M), Group: "Default pricing"),
        new("cost.pricing.modelPricing", ConfigSection.Cost, "Per-model pricing", "Source-generated JSON editor keyed by model name.", SettingKind.Dictionary, Group: "Model pricing"),
        new("cost.budget.enabled", ConfigSection.Cost, "Enforce daily budget", "Reject new inference after the daily USD limit is reached.", SettingKind.Bool, Group: "Daily budget"),
        new("cost.budget.dailyLimitUsd", ConfigSection.Cost, "Daily limit (USD)", "Maximum UTC-day spend before budget enforcement rejects inference.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.BudgetDailyLimitUsd), Group: "Daily budget"),

        // Daemon
        new("daemon.maxConcurrentJobs", ConfigSection.Daemon, "Concurrent jobs", "Maximum Unseen Servant jobs that may run simultaneously.", SettingKind.Int, 1, 1_024, 1, ClampName: nameof(ArcanumSettingClamps.DaemonMaxConcurrentJobs)),
        new("daemon.jobs.name", ConfigSection.Daemon, "Job name", "Human-readable schedule name.", SettingKind.String),
        new("daemon.jobs.intervalMinutes", ConfigSection.Daemon, "Interval (minutes)", "Minutes between scheduled runs.", SettingKind.Int, 1, 10_080, 1, ClampName: nameof(ArcanumSettingClamps.UnseenServantIntervalMinutes)),
        new("daemon.jobs.targetSpell", ConfigSection.Daemon, "Target spell", "Spell invoked on each schedule tick.", SettingKind.String),
        new("daemon.jobs.enabled", ConfigSection.Daemon, "Enabled", "Make this schedule eligible to run.", SettingKind.Bool),

        // CLI preferences
        new("cli.theme", ConfigSection.Cli, "Theme", "CLI color theme.", SettingKind.Enum, EnumType: typeof(ArcanumTheme)),
        new("cli.showManaBar", ConfigSection.Cli, "Show mana bar", "Show the persistent token-budget indicator in chat.", SettingKind.Bool),
    ];

    public static IReadOnlyDictionary<ConfigSection, IReadOnlyList<SettingDescriptor>> BySection { get; } =
        All.GroupBy(static descriptor => descriptor.Section)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<SettingDescriptor>)group.ToList());

    public static SettingDescriptor? Find(string key) =>
        All.FirstOrDefault(descriptor => descriptor.Key == key);

}
