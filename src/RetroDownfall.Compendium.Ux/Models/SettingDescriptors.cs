using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Compendium.Ux.Models;

public static class SettingDescriptors
{

    public static IReadOnlyList<SettingDescriptor> All { get; } =
    [

        // ===== Host =====

        new("edition", ConfigSection.Host, "Edition", "Runtime edition / hardening mode. Local (default) disables host process tools, A2A/Conclave advertising, and diagnostic MCP. Development unlocks those surfaces when accompanying startup flags are set. Overridable by ARCANUM_EDITION.", SettingKind.Enum, EnumType: typeof(ArcanumEdition)),

        new("host.port", ConfigSection.Host, "Port", "Kestrel listen port.", SettingKind.Int, 1, 65535, 1, ClampName: nameof(ArcanumSettingClamps.HostPort)),

        new("host.retainedLogFileCount", ConfigSection.Host, "Retained log file count", "Number of rolling log files kept on disk before oldest are pruned.", SettingKind.Int, 1, 366, 1, ClampName: nameof(ArcanumSettingClamps.RetainedLogFileCount)),

        new("host.enableEnterpriseTelemetry", ConfigSection.Host, "Enable enterprise telemetry", "When true, emits structured telemetry events to configured sinks.", SettingKind.Bool),

        new("host.corsAllowedOrigins", ConfigSection.Host, "CORS allowed origins", "Allowed origins for CORS. Use [\"*\"] to allow any origin (browser callers can read responses with the API key).", SettingKind.StringArray),

        new("host.enableScalarUi", ConfigSection.Host, "Enable Scalar UI", "When true, mounts the Scalar interactive API documentation UI at /api/scalar. The UI ships with inline JavaScript and CSS that conflict with strict CSP; default false.", SettingKind.Bool),

        new("host.systemFingerprint", ConfigSection.Host, "System fingerprint", "Optional stable identifier surfaced as system_fingerprint on OpenAI-shaped /v1/chat/completions responses. When null, the API derives one from the assembly version.", SettingKind.String, Placeholder: "arcanum-0.1.0-beta"),

        new("host.listenAny", ConfigSection.Host, "Listen on all interfaces", "When true (or ARCANUM_HOST_ANY), Kestrel binds HTTPS-only on the HTTPS port to all interfaces. Requires Host:Https:Enabled and a loadable certificate; plaintext any-IP HTTP is refused.", SettingKind.Bool),

        new("host.maxRequestBodyBytes", ConfigSection.Host, "Max request body bytes", "Kestrel MaxRequestBodySize in bytes. Default 10 MiB; clamped 256 KiB - 1 GiB.", SettingKind.Long, 262144, 1073741824, 1048576, ClampName: nameof(ArcanumSettingClamps.MaxRequestBodyBytes)),

        new("host.workspace", ConfigSection.Host, "Default workspace path", "Optional default workspace root for spell management and other workspace-scoped API routes. Prefer absolute paths.", SettingKind.Path, Placeholder: "/home/me/projects"),

        new("host.rateLimit.enabled", ConfigSection.Host, "Rate limit enabled", "When true, applies a fixed-window limiter to /api and /v1 endpoint groups, partitioned by API key (or IP when no key header is present).", SettingKind.Bool),

        new("host.rateLimit.permitLimit", ConfigSection.Host, "Rate limit permit limit", "Maximum requests permitted per window per partition. Default 120.", SettingKind.Int, 1, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.RateLimitPermitLimit)),

        new("host.rateLimit.windowSeconds", ConfigSection.Host, "Rate limit window seconds", "Window size in seconds for the fixed-window limiter. Default 60.", SettingKind.Int, 1, 86_400, 1, ClampName: nameof(ArcanumSettingClamps.RateLimitWindowSeconds)),

        new("host.rateLimit.queueLimit", ConfigSection.Host, "Rate limit queue limit", "Maximum queued requests per partition (served once the window resets). Default 0: excess requests are rejected with HTTP 429.", SettingKind.Int, 0, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.RateLimitQueueLimit)),

        // ===== Host — Audit log =====

        new("host.auditLog.enabled", ConfigSection.Host, "Audit log enabled", "Master toggle for the persisted inference audit log. When false (default), no file I/O occurs and GET /api/audit returns an empty list.", SettingKind.Bool),

        new("host.auditLog.filePath", ConfigSection.Host, "Audit log file path", "Base path; the directory is where dated audit-YYYYMMDD.jsonl files are written (one per UTC day).", SettingKind.Path, Placeholder: "~/.config/arcanum/audit.jsonl"),

        new("host.auditLog.maxSizeMb", ConfigSection.Host, "Audit log max size (MB)", "Soft per-day-file size cap; further writes for that day are dropped once reached. Default 100; clamped 10-1,000.", SettingKind.Int, 10, 1_000, 10, ClampName: nameof(ArcanumSettingClamps.HostAuditLogMaxSizeMb)),

        new("host.auditLog.retentionDays", ConfigSection.Host, "Audit log retention (days)", "Dated log files older than this are deleted automatically. Default 7; clamped 1-365.", SettingKind.Int, 1, 365, 1, ClampName: nameof(ArcanumSettingClamps.HostAuditLogRetentionDays)),

        new("host.auditLog.redactToolArguments", ConfigSection.Host, "Redact tool arguments", "When true (default), only tool names are captured. When false, raw tool argument JSON is also recorded — at the operator's risk, since arguments can carry file contents or command lines.", SettingKind.Bool),

        // ===== Host — HTTPS (TLS) =====

        new("host.https.enabled", ConfigSection.Host, "Enable HTTPS", "On loopback: when true, Kestrel adds a TLS listener alongside plaintext HTTP. Required true when ListenAny / ARCANUM_HOST_ANY is enabled (HTTPS-only any-IP). Default false.", SettingKind.Bool),

        new("host.https.port", ConfigSection.Host, "HTTPS port", "TLS listen port. Default 5443; clamped 1-65535. Must differ from the HTTP port.", SettingKind.Int, 1, 65535, 1, ClampName: nameof(ArcanumSettingClamps.HostHttpsPort)),

        new("host.https.certificatePath", ConfigSection.Host, "Certificate path", "PFX bundle path when no private key path is set, or a PEM certificate path when a private key path is provided. Leading ~ expands to the user profile directory.", SettingKind.Path, Placeholder: "~/.config/arcanum/certs/localhost.pfx"),

        new("host.https.privateKeyPath", ConfigSection.Host, "Private key path (PEM)", "Optional PEM private key path. When set, the certificate path is treated as a PEM certificate and the password is ignored. Leave blank to use a PFX bundle.", SettingKind.Path, Placeholder: "~/.config/arcanum/certs/localhost.key"),

        new("host.https.certificatePassword", ConfigSection.Host, "Certificate password (PFX)", "Password for the PFX bundle. Encrypted at rest with the dp:v1: prefix; ignored for PEM. Leave blank for a password-less PFX.", SettingKind.Secret),

        // ===== Host — Metrics =====

        new("metrics.enabled", ConfigSection.Metrics, "Enable metrics endpoint", "When true (default), GET /metrics renders Prometheus text format; when false, the endpoint returns 404.", SettingKind.Bool),

        new("metrics.requireApiKey", ConfigSection.Metrics, "Require API key for metrics", "When true (default), GET /metrics requires X-Arcanum-Key or Authorization: Bearer via ApiKeyEndpointFilter. Set false only for unauthenticated scrapes on a loopback-only bind; forced to effectively true whenever the host binds to all interfaces.", SettingKind.Bool),

        // ===== Server =====

        new("server.pidFilePath", ConfigSection.Server, "PID file path", "Path to the arcanum.pid file written by `arcanum serve`. Defaults to ~/.config/arcanum/arcanum.pid.", SettingKind.Path, Placeholder: "~/.config/arcanum/arcanum.pid"),

        // ===== Providers (top-level + per-provider rows) =====

        new("defaultModel", ConfigSection.Providers, "Default model", "Model used when a request does not specify one. Must match a models entry on some provider.", SettingKind.String, Placeholder: "deepseek-chat"),

        new("fastModel", ConfigSection.Providers, "Fast model", "Model used for internal background summarization, semantic routing preflight, and semantic Inquisitor judges. Must match a models entry on some provider.", SettingKind.String, Placeholder: "mistral:latest"),

        new("providers.name", ConfigSection.Providers, "Provider name", "Human-readable name for this provider entry.", SettingKind.String, Placeholder: "Local Ollama"),

        new("providers.type", ConfigSection.Providers, "Provider type", "Backend kind: OpenAICompatible (any OpenAI-shaped HTTP API). Ollama works via its OpenAI-compatible /v1 endpoint (for example http://localhost:11434/v1) — not as a native Ollama provider.", SettingKind.Enum, EnumType: typeof(AiProviderKind)),

        new("providers.endpoint", ConfigSection.Providers, "Endpoint", "Base URL for the provider API. Usually includes /v1 (for example Ollama: http://localhost:11434/v1).", SettingKind.String, Placeholder: "https://api.openai.com/v1"),

        new("providers.apiKey", ConfigSection.Providers, "API key", "Secret API key for this provider. Encrypted at rest with dp:v1: prefix; decrypted in the UI for editing. Leave blank for local OpenAI-compatible endpoints (for example Ollama).", SettingKind.Secret, Placeholder: "sk-..."),

        .. CreateTokenizationDescriptors("providers.tokenization", "Provider tokenization"),

        .. CreatePromptCachingDescriptors("providers.promptCaching", "Provider prompt caching"),

        new("providers.models.name", ConfigSection.Providers, "Model name", "Model ID advertised by this provider. Must include the DefaultModel and FastModel if those reference this provider.", SettingKind.String, Placeholder: "gpt-4o"),

        new("providers.models.supportsVision", ConfigSection.Providers, "Supports vision", "When true, this model accepts image content (Scrying). The Scrying capability gate rejects images to models where this is false.", SettingKind.Bool),

        .. CreateTokenizationDescriptors("providers.models.tokenization", "Model tokenization"),

        .. CreatePromptCachingDescriptors("providers.models.promptCaching", "Model prompt caching"),

        new("providers.models.reasoning.controlSupport", ConfigSection.Providers, "Reasoning controls", "Explicit reasoning controls accepted by this model: none, effort, budget, or effort and budget. Callers must still choose either effort or budget per request.", SettingKind.Enum, EnumType: typeof(ReasoningControlSupport)),

        new("providers.models.reasoning.supportsSummary", ConfigSection.Providers, "Supports reasoning summaries", "When true, the provider/model can return a client-safe summary separately from assistant answer text.", SettingKind.Bool),

        new("providers.models.reasoning.supportsFull", ConfigSection.Providers, "Supports full reasoning output", "When true, the provider/model can return client-safe full reasoning separately from assistant answer text. This never authorizes hidden or protected chain-of-thought disclosure.", SettingKind.Bool),

        new("providers.models.reasoning.supportsStreaming", ConfigSection.Providers, "Streams reasoning output", "When true, supported client-safe reasoning output may arrive incrementally while inference is running.", SettingKind.Bool),

        new("providers.models.reasoning.reportsReasoningTokens", ConfigSection.Providers, "Reports reasoning tokens", "When true, provider usage can report reasoning tokens as a subset of completion tokens.", SettingKind.Bool),

        new("providers.models.reasoning.allowsClientOutput", ConfigSection.Providers, "Allow client reasoning output", "Explicit permission for Arcanum to project provider-returned client-safe summaries or full reasoning. When false, summary/full output requests are rejected.", SettingKind.Bool),

        new("providers.models.reasoning.wireDialect", ConfigSection.Providers, "Reasoning wire dialect", "Explicit request-wire shape. Standard uses Microsoft.Extensions.AI/OpenAI options; numeric budgets require OpenRouter reasoning.max_tokens, top-level reasoning_budget, or Anthropic-style thinking.budget_tokens. Never inferred from provider/model names.", SettingKind.Enum, EnumType: typeof(ReasoningWireDialect)),

        new("providers.models.reasoning.maxBudgetTokens", ConfigSection.Providers, "Maximum reasoning budget tokens", "Optional per-model maximum for explicit numeric reasoning budgets. Valid only when budget control is supported.", SettingKind.Int, 1, 2_097_152, 1, ClampName: nameof(ArcanumSettingClamps.ReasoningBudgetTokens)),

        new("providers.contextWindowLimit", ConfigSection.Providers, "Context window limit", "Maximum tokens the hub will assemble into a single inference request for this provider. Clamp 256 - 2,097,152.", SettingKind.Int, 256, 2_097_152, 128, ClampName: nameof(ArcanumSettingClamps.ContextWindowLimit)),

        new("providers.supportsPromptCaching", ConfigSection.Providers, "Supports prompt caching", "When true (default for OpenAI-compatible providers), Arcanum records arcanum_prompt_cache_tokens metrics when the response usage reports cached prompt tokens. Set false for providers that do not support caching.", SettingKind.Bool),

        // ===== Intelligence =====

        new("intelligence.executeCommandTimeoutSeconds", ConfigSection.Intelligence, "Execute command timeout (s)", "Wall-clock timeout for in-process MCP execute_command and run_spell_script. Must be <= Mcp.RequestTimeoutSeconds.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.ExecuteCommandTimeoutSeconds)),

        new("intelligence.semanticRouterPreflightTimeoutSeconds", ConfigSection.Intelligence, "Semantic router preflight timeout (s)", "Timeout for the FastModel preflight call that decides whether semantic spell routing applies.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds)),

        new("intelligence.semanticRouterMaxTokens", ConfigSection.Intelligence, "Semantic router max tokens", "Maximum completion tokens for the semantic router preflight call.", SettingKind.Int, 1, 4096, 1, ClampName: nameof(ArcanumSettingClamps.SemanticRouterMaxTokens)),

        new("intelligence.semanticRouterTemperature", ConfigSection.Intelligence, "Semantic router temperature", "Sampling temperature for the semantic router preflight call.", SettingKind.Float, 0, 2, 0.1, ClampName: nameof(ArcanumSettingClamps.SemanticRouterTemperature)),

        new("intelligence.listDirectoryMaxPaths", ConfigSection.Intelligence, "List directory max paths", "Maximum file/dir entries returned by the list_directory tool in one call.", SettingKind.Int, 1, 2000, 1, ClampName: nameof(ArcanumSettingClamps.ListDirectoryMaxPaths)),

        new("intelligence.enableLoreSystem", ConfigSection.Intelligence, "Enable Lore system (legacy)", "Legacy/operator-only. No longer gates any MCP tool — the Lore MCP tools are removed. Retained for backward compatibility; /api/lore and arcanum lore still manage MageSettings as an operator key-value surface.", SettingKind.Bool),

        new("intelligence.enableLexiconSystem", ConfigSection.Intelligence, "Enable Lexicon system", "Gates the scribe_lexicon / delete_lexicon MCP tools and the Lexicon retrieval / DATA injection path. Default true (Option A): operators who previously disabled model-writable memory via EnableLoreSystem must now set this to false.", SettingKind.Bool),

        new("intelligence.lexiconMaxMatchedEntries", ConfigSection.Intelligence, "Lexicon max matched entries", "Maximum Lexicon entries returned per inference-turn MatchEntitiesAsync query.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.LexiconMaxMatchedEntries)),

        new("intelligence.lexiconMaxInjectedBytes", ConfigSection.Intelligence, "Lexicon max injected bytes", "Hard cap (bytes) on the rendered ### Lexicon (Known Context) DATA block in the Master system prompt.", SettingKind.Int, 256, 65536, 1, ClampName: nameof(ArcanumSettingClamps.LexiconMaxInjectedBytes)),

        new("intelligence.enableArchiveSearch", ConfigSection.Intelligence, "Enable archive search", "When true, the archive search tool can be invoked to query past sessions.", SettingKind.Bool),

        new("intelligence.archiveSearchMaxResults", ConfigSection.Intelligence, "Archive search max results", "Maximum number of past-session entries returned by an archive search query.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.ArchiveSearchMaxResults)),

        new("intelligence.archiveSearchMaxQueryLength", ConfigSection.Intelligence, "Archive search max query length", "Maximum character length of an archive search query string.", SettingKind.Int, 32, 4096, 1, ClampName: nameof(ArcanumSettingClamps.ArchiveSearchMaxQueryLength)),

        new("intelligence.campaignLogThreshold", ConfigSection.Intelligence, "Campaign log threshold", "Minimum importance level at which campaign events are persisted to the log.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.CampaignLogThreshold)),

        new("intelligence.campaignLogIdleTimeoutMinutes", ConfigSection.Intelligence, "Campaign log idle timeout (m)", "Minutes of idle time before an open campaign log channel is closed.", SettingKind.Int, 1, 43_200, 1, ClampName: nameof(ArcanumSettingClamps.CampaignLogIdleTimeoutMinutes)),

        new("intelligence.campaignLogSweepIntervalMinutes", ConfigSection.Intelligence, "Campaign log sweep interval (m)", "Minutes between sweeper passes that close idle campaign log channels.", SettingKind.Int, 1, 1440, 1, ClampName: nameof(ArcanumSettingClamps.CampaignLogSweepIntervalMinutes)),

        new("intelligence.contextWindowCompressionThreshold", ConfigSection.Intelligence, "Context compression threshold (%)", "Percentage of the context window at which read-time compression is triggered.", SettingKind.Int, 50, 100, 1, ClampName: nameof(ArcanumSettingClamps.ContextWindowCompressionThreshold)),

        new("intelligence.enableContextCompression", ConfigSection.Intelligence, "Enable context compression", "When true, older session entries are summarized near the context limit (rows are never deleted).", SettingKind.Bool),

        new("intelligence.enableTokenTracking", ConfigSection.Intelligence, "Enable token tracking", "When true, token counts are tracked per inference turn and surfaced in usage stats.", SettingKind.Bool),

        new("intelligence.toolOutputCapBytes", ConfigSection.Intelligence, "Tool output cap (bytes)", "Hard cap on captured stdout/stderr for in-process execute_command and run_spell_script. Output beyond this is truncated.", SettingKind.Long, 65_536, 67_108_864, 65_536, ClampName: nameof(ArcanumSettingClamps.ToolOutputCapBytes)),

        new("intelligence.disconnectPolicy", ConfigSection.Intelligence, "Disconnect policy", "Auto (default): continue-then-replay when Idempotency-Key is present; otherwise cancel→Abandoned. See ADR 0003.", SettingKind.Enum, EnumType: typeof(DisconnectPolicy)),

        new("intelligence.reservedOutputTokens", ConfigSection.Intelligence, "Reserved output tokens", "Tokens reserved for model output during per-call context preflight when MaxOutputTokens is unset (default 1024).", SettingKind.Int, 0, 128000, 1, ClampName: nameof(ArcanumSettingClamps.ReservedOutputTokens)),

        new("intelligence.maxToolInferenceRounds", ConfigSection.Intelligence, "Max tool inference rounds", "Maximum agentic tool rounds per inference turn (default 8). Beyond this the turn fails with Hub.ToolLoop.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxToolInferenceRounds)),

        new("intelligence.tolerateToolFailures", ConfigSection.Intelligence, "Tolerate tool failures", "When true (default), an unexpected tool exception during a buffered turn is caught and synthesized into a tool result instead of failing the whole turn with Hub.Error. Streaming already tolerates failures unconditionally.", SettingKind.Bool),

        new("intelligence.compressionPreflightMinMessages", ConfigSection.Intelligence, "Manual compact min messages", "Minimum history size before the explicit compact operation tokenizes and prunes entries. Live calls always measure materialized context.", SettingKind.Int, 0, 100, 1, ClampName: nameof(ArcanumSettingClamps.CompressionPreflightMinMessages)),

        new("intelligence.perMessageTemplateOverheadTokens", ConfigSection.Intelligence, "Fallback per-message framing (tokens)", "Default role/template framing for calibrated and unknown tokenization profiles; a provider/model profile may override it.", SettingKind.Int, 0, 32, 1, ClampName: nameof(ArcanumSettingClamps.PerMessageTemplateOverheadTokens)),

        new("intelligence.tokenizerEncoding", ConfigSection.Intelligence, "Tokenizer encoding", "Fallback Tiktoken encoding for unknown or calibrated model profiles. Known and explicitly configured exact profiles resolve independently.", SettingKind.String, Placeholder: "o200k_base"),

        new("intelligence.estimatedTokenSafetyMarginPercent", ConfigSection.Intelligence, "Estimated-token safety margin (%)", "Percentage added to calibrated and unknown input-token estimates. Exact local tokenizers are not inflated.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.EstimatedTokenSafetyMarginPercent)),

        new("intelligence.unknownImageTokenReserve", ConfigSection.Intelligence, "Unknown image token reserve", "Conservative per-image reserve when no provider/model-specific image formula is available. Image byte size is never presented as an exact token count.", SettingKind.Int, 1, 128_000, 1, ClampName: nameof(ArcanumSettingClamps.UnknownImageTokenReserve)),

        new("intelligence.maxOpenApiMessages", ConfigSection.Intelligence, "Max OpenAI API messages", "Maximum messages accepted in a single /v1/chat/completions request body.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.MaxOpenApiMessages)),

        new("intelligence.maxStatelessMessages", ConfigSection.Intelligence, "Max stateless messages", "Maximum messages accepted in a single stateless /api/intelligence/ping request body.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.MaxStatelessMessages)),

        new("intelligence.maxContentPartsPerMessage", ConfigSection.Intelligence, "Max content parts per message", "Maximum content parts accepted within a single multimodal message.", SettingKind.Int, 1, 1024, 1, ClampName: nameof(ArcanumSettingClamps.MaxContentPartsPerMessage)),

        new("intelligence.maxPingPromptChars", ConfigSection.Intelligence, "Max ping prompt chars", "Maximum character length of the prompt in a single /api/intelligence/ping request.", SettingKind.Int, 1, 262_144, 1024, ClampName: nameof(ArcanumSettingClamps.MaxPingPromptChars)),

        new("intelligence.maxPlanSteps", ConfigSection.Intelligence, "Max plan steps", "Maximum steps in an Apprentice plan before the plan is rejected.", SettingKind.Int, 1, 200, 1, ClampName: nameof(ArcanumSettingClamps.MaxPlanSteps)),

        new("intelligence.inferenceTimeoutSeconds", ConfigSection.Intelligence, "Inference timeout (s)", "Wall-clock timeout for a single inference turn (buffered or streaming), including tool rounds. Default 600.", SettingKind.Int, 5, 3600, 1, ClampName: nameof(ArcanumSettingClamps.InferenceTimeoutSeconds)),

        new("structuredOutput.enabled", ConfigSection.StructuredOutput, "Structured output enabled", "When true (default), responses requesting response_format: json_schema are validated and retried on failure.", SettingKind.Bool),

        new("structuredOutput.maxValidationRetries", ConfigSection.StructuredOutput, "Max validation retries", "Maximum retry attempts after a structured-output validation failure. Default 2; clamped 0-5.", SettingKind.Int, 0, 5, 1, ClampName: nameof(ArcanumSettingClamps.StructuredOutputMaxValidationRetries)),

        new("structuredOutput.useProviderConstrainedDecoding", ConfigSection.StructuredOutput, "Use provider constrained decoding", "When true (default), Arcanum asks the provider to constrain decoding (strict: true for OpenAI-compatible).", SettingKind.Bool),

        new("structuredOutput.strictMode", ConfigSection.StructuredOutput, "Strict mode", "When true, a response that fails schema validation after all retries is rejected with 400. When false (default), the response is returned with a warning header.", SettingKind.Bool),

        new("structuredOutput.schemaMaxDepth", ConfigSection.StructuredOutput, "Schema max depth", "Maximum recursion depth for JSON Schema parsing and validation. Default 10; clamped 1-50.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.JsonSchemaMaxDepth)),

        new("intelligence.useFastModelForSpellRouting", ConfigSection.Intelligence, "Use fast model for spell routing", "When true, semantic spell-router preflight uses the configured FastModel.", SettingKind.Bool),

        // ===== Mcp =====

        new("mcp.requestTimeoutSeconds", ConfigSection.Mcp, "Request timeout (s)", "Per-request JSON-RPC timeout for MCP tool calls. Must be >= Intelligence.ExecuteCommandTimeoutSeconds.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.McpRequestTimeoutSeconds)),

        new("mcp.maxPaginationPages", ConfigSection.Mcp, "Max pagination pages", "Maximum pages fetched during a tools/list pagination walk.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.McpMaxPaginationPages)),

        new("mcp.bootstrapBlocksStartup", ConfigSection.Mcp, "Bootstrap blocks startup", "When true, the host waits for all bootstrap MCP servers to be ready before serving traffic.", SettingKind.Bool),

        new("mcp.maxServers", ConfigSection.Mcp, "Max servers", "Maximum concurrently registered MCP servers.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.McpMaxServers)),

        new("mcp.maxToolsPerServer", ConfigSection.Mcp, "Max tools per server", "Maximum tools accepted from a single MCP server during tools/list.", SettingKind.Int, 1, 2048, 1, ClampName: nameof(ArcanumSettingClamps.McpMaxToolsPerServer)),

        new("mcp.maxToolsPerListPage", ConfigSection.Mcp, "Max tools per list page", "Maximum tools accepted in a single tools/list page response.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.McpMaxToolsPerListPage)),

        new("mcp.maxToolsTotalBytes", ConfigSection.Mcp, "Max tools total (bytes)", "Maximum total bytes of tool schema JSON accepted across all servers.", SettingKind.Int, 65_536, 16_777_216, 65_536, ClampName: nameof(ArcanumSettingClamps.McpMaxToolsTotalBytes)),

        new("mcp.maxJsonRpcLineBytes", ConfigSection.Mcp, "Max JSON-RPC line (bytes)", "Maximum single-line JSON-RPC frame size. Must be large enough for Intelligence.ToolOutputCapBytes after envelope and escaping.", SettingKind.Int, 65_536, 8_388_608, 65_536, ClampName: nameof(ArcanumSettingClamps.McpMaxJsonRpcLineBytes)),

        new("mcp.httpRequestTimeoutSeconds", ConfigSection.Mcp, "HTTP request timeout (s)", "Timeout for the named HttpClient(McpHttp) used by the Streamable HTTP transport (headers phase).", SettingKind.Int, 10, 600, 1, ClampName: nameof(ArcanumSettingClamps.McpHttpRequestTimeoutSeconds)),

        new("mcp.allowedHttpHosts", ConfigSection.Mcp, "Allowed HTTP hosts", "Hosts permitted over plaintext http for Streamable HTTP MCP servers (e.g. localhost for a trusted dev gateway). Remote HTTP servers must use https.", SettingKind.StringArray),

        // ===== Orchestration — Daemon =====

        new("daemon.maxConcurrentJobs", ConfigSection.Orchestration, "Daemon max concurrent jobs", "Caps the number of Unseen Servant jobs that can run concurrently.", SettingKind.Int, 1, 1024, 1, ClampName: nameof(ArcanumSettingClamps.DaemonMaxConcurrentJobs)),

        new("daemon.shutdownDrainTimeoutSeconds", ConfigSection.Orchestration, "Daemon shutdown drain (s)", "Maximum time StopAsync waits for in-flight jobs to drain after shutdown begins; 0 means no wait.", SettingKind.Int, 0, 600, 1, ClampName: nameof(ArcanumSettingClamps.DaemonShutdownDrainTimeoutSeconds)),

        new("daemon.executionHistoryLimit", ConfigSection.Orchestration, "Execution history limit", "Maximum execution records retained per daemon in the in-memory history store.", SettingKind.Int, 10, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.DaemonExecutionHistoryLimit)),

        // ===== Orchestration — UnseenServantJob (per-row) =====

        new("daemon.jobs.name", ConfigSection.Orchestration, "Job name", "Human-readable name for this Unseen Servant job.", SettingKind.String),

        new("daemon.jobs.intervalMinutes", ConfigSection.Orchestration, "Job interval (m)", "Minutes between scheduled runs of this job.", SettingKind.Int, 1, 10_080, 1, ClampName: nameof(ArcanumSettingClamps.UnseenServantIntervalMinutes)),

        new("daemon.jobs.targetSpell", ConfigSection.Orchestration, "Target spell", "Spell invoked by this job on each tick.", SettingKind.String, Placeholder: "lore-sweep"),

        new("daemon.jobs.enabled", ConfigSection.Orchestration, "Job enabled", "When true, this job is eligible to run on its schedule.", SettingKind.Bool),

        // ===== Orchestration — Apprentices =====

        new("apprentices.enabled", ConfigSection.Orchestration, "Apprentices enabled", "Master toggle for the Apprentice autonomous sub-agent subsystem.", SettingKind.Bool),

        new("apprentices.maxConcurrentApprentices", ConfigSection.Orchestration, "Max concurrent apprentices", "Maximum Apprentices running simultaneously.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.MaxConcurrentApprentices)),

        new("apprentices.stepTimeoutMinutes", ConfigSection.Orchestration, "Step timeout (m)", "Per-step timeout for an Apprentice's plan execution.", SettingKind.Int, 5, 120, 1, ClampName: nameof(ArcanumSettingClamps.StepTimeoutMinutes)),

        new("apprentices.chronicleChannelCapacity", ConfigSection.Orchestration, "Chronicle channel capacity", "Per-subscriber bounded channel capacity for Chronicle and session event hubs. Applied when a hub is first created.", SettingKind.Int, 100, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.ChronicleChannelCapacity)),

        new("apprentices.maxStepRetries", ConfigSection.Orchestration, "Max step retries", "Maximum retries per failed Apprentice step (Second Wind).", SettingKind.Int, 0, 10, 1, ClampName: nameof(ArcanumSettingClamps.MaxStepRetries)),

        new("apprentices.retryBackoffSeconds", ConfigSection.Orchestration, "Retry backoff (s)", "Initial exponential-backoff seconds between retries.", SettingKind.Int, 1, 300, 1, ClampName: nameof(ArcanumSettingClamps.RetryBackoffSeconds)),

        new("apprentices.retryBackoffMaxSeconds", ConfigSection.Orchestration, "Retry backoff max (s)", "Cap for exponential-backoff seconds between retries.", SettingKind.Int, 1, 3600, 1, ClampName: nameof(ArcanumSettingClamps.RetryBackoffMaxSeconds)),

        new("apprentices.enableShiftingFate", ConfigSection.Orchestration, "Enable Shifting Fate", "When true, a failed step can trigger a plan re-weave (Shifting Fate).", SettingKind.Bool),

        new("apprentices.enableDivineIntervention", ConfigSection.Orchestration, "Enable Divine Intervention", "When true, an Escalated Apprentice surfaces for Divine Intervention via /api/apprentices/{id}/intervene.", SettingKind.Bool),

        new("apprentices.maxSimulacra", ConfigSection.Orchestration, "Max simulacra", "Maximum parallel Simulacrum steps within a single Apprentice plan step.", SettingKind.Int, 1, 10, 1, ClampName: nameof(ArcanumSettingClamps.MaxSimulacra)),

        new("apprentices.maxRunSteps", ConfigSection.Orchestration, "Max run steps", "Maximum steps in a single Apprentice run before forced termination.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxRunSteps)),

        new("apprentices.maxRunDurationMinutes", ConfigSection.Orchestration, "Max run duration (m)", "Maximum wall-clock minutes for a single Apprentice run.", SettingKind.Int, 5, 10_080, 1, ClampName: nameof(ArcanumSettingClamps.MaxRunDurationMinutes)),

        new("apprentices.maxReweavesPerRun", ConfigSection.Orchestration, "Max reweaves per run", "Maximum Shifting Fate plan re-weaves allowed in a single Apprentice run.", SettingKind.Int, 0, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxReweavesPerRun)),

        new("apprentices.maxPendingStarts", ConfigSection.Orchestration, "Max pending starts", "Maximum Apprentices waiting to start before new start requests are rejected.", SettingKind.Int, 1, 1000, 1, ClampName: nameof(ArcanumSettingClamps.MaxPendingStarts)),

        // ===== Orchestration — Conclave =====

        new("conclave.enabled", ConfigSection.Orchestration, "Conclave enabled", "Gates cross-Apprentice delegation (Cast Sending). When false, the cast_sending tool is not advertised and /api/apprentices/{id}/cast refuses delegation.", SettingKind.Bool),

        new("conclave.maxDelegationDepth", ConfigSection.Orchestration, "Max delegation depth", "Maximum delegation depth from a Conclave root Apprentice (0 = root only, no children).", SettingKind.Int, 0, 20, 1, ClampName: nameof(ArcanumSettingClamps.MaxDelegationDepth)),

        new("conclave.maxDescendantsPerRoot", ConfigSection.Orchestration, "Max descendants per root", "Maximum total descendant Apprentices allowed under one Conclave root.", SettingKind.Int, 1, 200, 1, ClampName: nameof(ArcanumSettingClamps.MaxDescendantsPerRoot)),

        // ===== Orchestration — Conclave A2A (Agent-to-Agent) =====

        new("conclave.a2A.enabled", ConfigSection.Orchestration, "A2A enabled", "Master toggle gating both the A2A server and client surfaces. Also requires Conclave enabled to be true.", SettingKind.Bool),

        new("conclave.a2A.serverEnabled", ConfigSection.Orchestration, "A2A server enabled", "Exposes Arcanum Apprentices as an A2A server (inbound tasks from external agents).", SettingKind.Bool),

        new("conclave.a2A.serverPath", ConfigSection.Orchestration, "A2A server path", "HTTP path under which the A2A server endpoints and authenticated Agent Card (\"Heraldry\") are mapped.", SettingKind.String, Placeholder: "/api/conclave/a2a"),

        new("conclave.a2A.agentCardName", ConfigSection.Orchestration, "Agent Card name", "Display name advertised on the A2A Agent Card (\"Heraldry\").", SettingKind.String),

        new("conclave.a2A.agentCardDescription", ConfigSection.Orchestration, "Agent Card description", "Display description advertised on the A2A Agent Card (\"Heraldry\").", SettingKind.String),

        new("conclave.a2A.clientEnabled", ConfigSection.Orchestration, "A2A client enabled", "Advertises and enables the in-process dispatch_sending MCP tool so an Apprentice can delegate to an external A2A agent.", SettingKind.Bool),

        new("conclave.a2A.maxExternalTasks", ConfigSection.Orchestration, "Max external tasks", "Maximum number of concurrently in-flight external (client-side) A2A delegations.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxExternalTasks)),

        new("conclave.a2A.externalTaskTimeoutMinutes", ConfigSection.Orchestration, "External task timeout (min)", "Per-delegation timeout, in minutes, for a client-side dispatch_sending call.", SettingKind.Int, 5, 1440, 5, ClampName: nameof(ArcanumSettingClamps.ExternalTaskTimeoutMinutes)),

        new("conclave.a2A.allowedRemoteAgents", ConfigSection.Orchestration, "Allowed remote agents", "Allowlist of remote Agent Card URLs (or origins) that dispatch_sending may target. Empty means any URL is a candidate, subject to the outbound SSRF guard, which always applies regardless of this allowlist.", SettingKind.StringArray),

        new("conclave.a2A.defaultWorkspace", ConfigSection.Orchestration, "Default workspace", "Fallback workspace path for inbound A2A tasks (server side) when the request carries no workspace or campaign hint. Empty falls back to Arcanum:Host:Workspace, then the process's current directory.", SettingKind.String),

        // ===== Security — Ward =====

        new("ward.enabled", ConfigSection.Security, "Wards enabled", "Master toggle for the Forbidden Arts approval gate.", SettingKind.Bool),

        new("ward.forbiddenArts", ConfigSection.Security, "Forbidden arts", "Tool names gated by the Ward approval flow before execution.", SettingKind.StringArray),

        new("ward.timeoutSeconds", ConfigSection.Security, "Ward timeout (s)", "Seconds before a pending Ward approval auto-expires.", SettingKind.Int, 10, 600, 1, ClampName: nameof(ArcanumSettingClamps.WardTimeoutSeconds)),

        new("ward.maxActiveWards", ConfigSection.Security, "Max active wards", "Maximum simultaneously pending Ward approvals.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxActiveWards)),

        new("ward.autoDenyInUnattendedMode", ConfigSection.Security, "Auto-deny in unattended mode", "When true and the host is unattended, Ward approvals are auto-denied instead of hanging.", SettingKind.Bool),

        new("ward.unattendedMode", ConfigSection.Security, "Unattended mode", "Default for operator-facing chat (Command Center, ask/chat without --unattended). When true, Forbidden Arts follow auto-deny instead of placing wards. Daemons and other headless paths always run unattended.", SettingKind.Bool),

        // ===== Security — API key =====

        new("security.maxApiKeyHeaderUtf16Chars", ConfigSection.Security, "Max API key header chars", "Maximum UTF-16 char length accepted in the X-Arcanum-Key / Authorization header.", SettingKind.Int, 128, 8192, 16, ClampName: nameof(ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars)),

        new("security.apiKeyCacheTtlSeconds", ConfigSection.Security, "API key cache TTL (s)", "TTL for the in-memory cache of the expected API key digest. After this window, on-disk rotation takes effect without a restart.", SettingKind.Int, 1, 3600, 1, ClampName: nameof(ArcanumSettingClamps.ApiKeyCacheTtlSeconds)),

        new("security.idempotencyTtlHours", ConfigSection.Security, "Idempotency-Key TTL (hours)", "How long a cached Idempotency-Key response is replayed before it is treated as expired.", SettingKind.Int, 1, 168, 1, ClampName: nameof(ArcanumSettingClamps.SecurityIdempotencyTtlHours)),

        new("security.idempotencyMaxResponseBytes", ConfigSection.Security, "Idempotency-Key max cached response (bytes)", "Maximum buffered response size cached for an Idempotency-Key request; larger responses still stream fully to the client but are never cached.", SettingKind.Int, 1024 * 1024, 100 * 1024 * 1024, 1024 * 1024, ClampName: nameof(ArcanumSettingClamps.SecurityIdempotencyMaxResponseBytes)),

        new("security.allowUnsandboxedToolChildren", ConfigSection.Security, "Allow unsandboxed tool children", "When false (default), execute_command and run_spell_script require an OS filesystem jail where active for this beta (macOS sandbox-exec). Linux Landlock is inactive (fail-closed). Setup failure refuses the tool rather than running unbounded. When true, logs a warning and runs without the FS jail (resource limits still apply). Filesystem-only — does not isolate network. Windows has no FS jail; Sanctum path-boundary enforcement still denies these tools (escape hatch does not bypass).", SettingKind.Bool),

        // ===== CommLink =====

        new("commLink.webhookUrl", ConfigSection.CommLink, "Webhook URL", "Outbound URL POSTed to on Comm Link alerts. Defaults to https; add http to AllowedSchemes to use plaintext.", SettingKind.Secret, Placeholder: "https://hooks.example.com/arcanum"),

        new("commLink.webhookTimeoutSeconds", ConfigSection.CommLink, "Webhook timeout (s)", "Timeout for the named HttpClient(CommLinkWebhook) used to POST alerts.", SettingKind.Int, 1, 120, 1, ClampName: nameof(ArcanumSettingClamps.WebhookTimeoutSeconds)),

        new("commLink.allowedSchemes", ConfigSection.CommLink, "Allowed schemes", "URI schemes the webhook dispatcher is permitted to call. Default [https]. Add http to opt in to plaintext.", SettingKind.StringArray),

        new("commLink.allowedHosts", ConfigSection.CommLink, "Allowed hosts", "Optional allowed webhook hosts (e.g. hooks.example.com). When populated, any URL whose host is not listed is rejected at startup.", SettingKind.StringArray),

        // ===== Storage — Grimoire =====

        new("grimoire.maxMessagesPerConversationLoad", ConfigSection.Storage, "Max messages per conversation load", "Maximum entries loaded into memory for a single GetSessionAsync hydration; bounds RAM on long threads.", SettingKind.Int, 50, 5000, 1, ClampName: nameof(ArcanumSettingClamps.MaxMessagesPerConversationLoad)),

        new("grimoire.workspaceContextRetentionCount", ConfigSection.Storage, "Workspace context retention", "Number of Chronosync WorkspaceContext snapshots retained per workspace path before older rows are purged.", SettingKind.Int, 1, 1000, 1, ClampName: nameof(ArcanumSettingClamps.WorkspaceContextRetentionCount)),

        new("grimoire.defaultLoreListLimit", ConfigSection.Storage, "Default Lore list limit", "Default page size for GET /api/lore when limit is omitted.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.ListQueryLimit)),

        // ===== Storage — Sessions =====

        new("sessions.defaultQueryLimit", ConfigSection.Storage, "Default query limit", "Default page size for session entry queries when limit is omitted.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.SessionQueryLimit)),

        new("sessions.maxStreamReplayEntries", ConfigSection.Storage, "Max stream replay entries", "Maximum entries replayed to a newly connected session SSE subscriber.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.SessionStreamReplayLimit)),

        new("sessions.maxEntriesPerSession", ConfigSection.Storage, "Max entries per session", "Maximum entries allowed in a single session before inserts are rejected.", SettingKind.Int, 100, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.MaxEntriesPerSession)),

        new("sessions.maxEntryContentBytes", ConfigSection.Storage, "Max entry content (bytes)", "Maximum byte size of a single session entry's content.", SettingKind.Int, 1024, 16_777_216, 1024, ClampName: nameof(ArcanumSettingClamps.MaxEntryContentBytes)),

        new("sessions.maxForkDepth", ConfigSection.Storage, "Max fork depth", "Maximum number of ancestor forks allowed in a session's lineage chain before further forking is rejected.", SettingKind.Int, 0, 20, 1, ClampName: nameof(ArcanumSettingClamps.MaxForkDepth)),

        new("sessions.allowMemoryManagement", ConfigSection.Storage, "Allow memory management", "When true, enables DELETE /entries, pin/unpin, and compact endpoints for manual conversation memory management.", SettingKind.Bool),

        new("sessions.maxPinnedEntries", ConfigSection.Storage, "Max pinned entries", "Maximum pinned entries per session. Pinned entries are always included in inference context even when compression would otherwise drop them.", SettingKind.Int, 0, 100, 1, ClampName: nameof(ArcanumSettingClamps.SessionMaxPinnedEntries)),

        new("files.maxUploadSizeBytes", ConfigSection.Files, "Max file upload size (bytes)", "Maximum upload size for POST /v1/files.", SettingKind.Long, 1024 * 1024, 10L * 1024 * 1024 * 1024, 1024 * 1024, ClampName: nameof(ArcanumSettingClamps.FilesMaxUploadSizeBytes)),

        new("files.allowedMimeTypes", ConfigSection.Files, "Allowed upload MIME types", "Allowed Content-Type values for POST /v1/files uploads. Empty (default) means no operator-configured restriction.", SettingKind.StringArray),

        new("attachments.enabled", ConfigSection.Attachments, "Enabled", "When true, session attachments (text + Scrying images) are persisted on disk with Grimoire metadata.", SettingKind.Bool),

        new("attachments.maxReferencesPerTurn", ConfigSection.Attachments, "Max references per turn", "Combined per-turn budget for user AttachmentReferences and model attach_session_file injections.", SettingKind.Int, 1, 32, 1, ClampName: nameof(ArcanumSettingClamps.AttachmentsMaxReferencesPerTurn)),

        new("attachments.maxVersionsPerLogicalKey", ConfigSection.Attachments, "Max versions per logical key", "Soft cap on versioned copies of the same logical attachment name within a session.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.AttachmentsMaxVersionsPerLogicalKey)),

        new("attachments.maxBytesPerSession", ConfigSection.Attachments, "Max bytes per session", "Soft byte budget for all attachment files in a single session.", SettingKind.Long, 1024 * 1024, 10L * 1024 * 1024 * 1024, 1024 * 1024, ClampName: nameof(ArcanumSettingClamps.AttachmentsMaxBytesPerSession)),

        new("attachments.pendingRetentionHours", ConfigSection.Attachments, "Pending retention (hours)", "Age after which stale pending attachment rows and _pending directories are garbage-collected.", SettingKind.Int, 1, 168, 1, ClampName: nameof(ArcanumSettingClamps.AttachmentsPendingRetentionHours)),

        new("attachments.maxIndexItemsInPrompt", ConfigSection.Attachments, "Max index items in prompt", "Maximum attachment index entries injected into the system prompt.", SettingKind.Int, 1, 200, 1, ClampName: nameof(ArcanumSettingClamps.AttachmentsMaxIndexItemsInPrompt)),

        new("attachments.maxIndexBytesInPrompt", ConfigSection.Attachments, "Max index bytes in prompt", "Maximum UTF-16 budget for the Session Attachments Index block in the system prompt.", SettingKind.Int, 256, 64_000, 256, ClampName: nameof(ArcanumSettingClamps.AttachmentsMaxIndexBytesInPrompt)),

        new("attachments.enableModelAttachTool", ConfigSection.Attachments, "Enable model attach tool", "When true, advertises the internal MCP attach_session_file tool for the current session.", SettingKind.Bool),

        new("batches.maxConcurrentBatches", ConfigSection.Batches, "Max concurrent batches", "Maximum number of /v1/batches processed concurrently across the whole server.", SettingKind.Int, 1, 20, 1, ClampName: nameof(ArcanumSettingClamps.BatchesMaxConcurrentBatches)),

        new("batches.maxRequestsPerBatch", ConfigSection.Batches, "Max requests per batch", "Maximum JSONL request lines accepted in a single /v1/batches input file.", SettingKind.Int, 1, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.BatchesMaxRequestsPerBatch)),

        new("batches.batchExpiryHours", ConfigSection.Batches, "Batch expiry (hours)", "How long after creation a non-terminal batch is force-expired (input/output files deleted).", SettingKind.Int, 1, 168, 1, ClampName: nameof(ArcanumSettingClamps.BatchesBatchExpiryHours)),

        new("batches.maxConcurrentRequestsPerBatch", ConfigSection.Batches, "Max concurrent requests per batch", "Maximum chat-completion requests run concurrently within a single batch.", SettingKind.Int, 1, 10, 1, ClampName: nameof(ArcanumSettingClamps.BatchesMaxConcurrentRequestsPerBatch)),

        // ===== Storage — EventBus =====

        new("eventBus.channelCapacity", ConfigSection.Storage, "Channel capacity", "Per-subscriber bounded channel capacity for live SSE push updates. When full, DropOldest discards the oldest frame.", SettingKind.Int, 64, 65_536, 1, ClampName: nameof(ArcanumSettingClamps.EventBusChannelCapacity)),

        new("eventBus.heartbeatSeconds", ConfigSection.Storage, "Heartbeat (s)", "SSE keep-alive comment interval for /api/events/*, session stream, and Chronicle. 0 disables heartbeats.", SettingKind.Int, 0, 300, 1, ClampName: nameof(ArcanumSettingClamps.EventBusHeartbeatSeconds)),

        new("eventBus.maxSseConnections", ConfigSection.Storage, "Max SSE connections", "Maximum concurrent SSE connections across all event streams; excess requests get 503 Api.TooManyConnections.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxSseConnections)),

        new("eventBus.maxSseConnectionsPerType", ConfigSection.Storage, "Max SSE connections per type", "Maximum concurrent SSE connections per event-type family (daemon, MCP, logs, session, Chronicle); guarantees a fair share of the global pool so one stream family cannot starve the others.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.SseConnectionsPerType)),

        // ===== Storage — Logs =====

        new("logs.ringBufferCapacity", ConfigSection.Storage, "Ring buffer capacity", "Capacity of the in-memory log ring buffer. Read once at construction; changes require a restart.", SettingKind.Int, 1000, 100_000, 1000, ClampName: nameof(ArcanumSettingClamps.LogRingBufferCapacity)),

        new("logs.minLevelInBuffer", ConfigSection.Storage, "Min level in buffer", "Minimum log level retained in the in-memory ring buffer.", SettingKind.Enum, EnumType: typeof(LogLevel)),

        // ===== Storage — Workspaces =====

        new("workspaces.maxFileReadSizeBytes", ConfigSection.Storage, "Max file read (bytes)", "Maximum byte size of a single workspace file read via the API.", SettingKind.Long, 1024, 10_485_760, 1024, ClampName: nameof(ArcanumSettingClamps.MaxFileReadSizeBytes)),

        new("workspaces.listDirectoryMaxDepth", ConfigSection.Storage, "List directory max depth", "Maximum directory depth for recursive workspace file listing.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.ListDirectoryMaxDepth)),

        new("workspaces.enableFileWrite", ConfigSection.Storage, "Enable file write", "Master toggle for the workspace file write/modify/delete API (PUT/PATCH/DELETE .../files, POST .../files/directory). When false (default), every write/modify/delete endpoint returns 403 Workspace.FileWriteDisabled without performing any I/O.", SettingKind.Bool),

        new("workspaces.maxFileWriteSizeBytes", ConfigSection.Storage, "Max file write (bytes)", "Maximum byte size of file content accepted by PUT /api/workspaces/{id}/files/contents (and the newString on PATCH .../files/contents).", SettingKind.Long, 1024, 10_485_760, 1024, ClampName: nameof(ArcanumSettingClamps.MaxFileWriteSizeBytes)),

        new("workspaces.maxReplaceTextBlockBytes", ConfigSection.Storage, "Max replace text block (bytes)", "Maximum combined byte size of oldString + newString on PATCH /api/workspaces/{id}/files/contents.", SettingKind.Long, 1024, 4_194_304, 1024, ClampName: nameof(ArcanumSettingClamps.MaxReplaceTextBlockBytes)),

        // ===== Forge — Perception =====

        new("perception.maxEnumerationSteps", ConfigSection.Forge, "Perception max enumeration steps", "Maximum file/dir entries enumerated by a single Eye of the World look request.", SettingKind.Int, 1, 10_000_000, 1000, ClampName: nameof(ArcanumSettingClamps.MaxEnumerationSteps)),

        new("perception.maxTableOfContentsLines", ConfigSection.Forge, "Perception max TOC lines", "Maximum lines in the table-of-contents summary produced by a look request.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxTableOfContentsLines)),

        new("perception.allowedWorkspaceRoots", ConfigSection.Forge, "Perception allowed roots", "Absolute directory roots that GET /api/perception/look may scan. Empty (default) denies all requests with 403 Perception.PathNotAllowed.", SettingKind.StringArray),

        // ===== Forge — Spells =====

        new("spells.allowedWorkspaceRoots", ConfigSection.Forge, "Spells allowed roots", "Absolute directory roots that spell CRUD routes may use. Empty denies all access by default.", SettingKind.StringArray),

        new("spells.maxFileSizeBytes", ConfigSection.Forge, "Spell max file size (bytes)", "Maximum SPELL.md (and frontmatter) read size in bytes. Further capped by Workspaces.MaxFileReadSizeBytes.", SettingKind.Long, 1024, 1_048_576, 1024, ClampName: nameof(ArcanumSettingClamps.SpellMaxFileSizeBytes)),

        new("spells.metadataScanCacheTtlSeconds", ConfigSection.Forge, "Metadata scan cache TTL (s)", "TTL for the in-process spell-metadata scan cache used by routing and Arcane Resonance. 0 disables caching.", SettingKind.Int, 0, 300, 1, ClampName: nameof(ArcanumSettingClamps.MetadataScanCacheTtlSeconds)),

        new("spells.maxDependencies", ConfigSection.Forge, "Max dependencies", "Maximum dependencies a single spell may declare in SPELL.json.", SettingKind.Int, 0, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxDependencies)),

        new("spells.maxDeclaredTools", ConfigSection.Forge, "Max declared tools", "Maximum tools a single spell may declare in SPELL.json (Artifact Attunement allowlist).", SettingKind.Int, 0, 256, 1, ClampName: nameof(ArcanumSettingClamps.MaxDeclaredTools)),

        new("spells.maxResonantDependencies", ConfigSection.Forge, "Max resonant dependencies", "Maximum Arcane Resonance dependencies resolved recursively per spell (hard depth limit 3, cycle-safe).", SettingKind.Int, 0, 50, 1, ClampName: nameof(ArcanumSettingClamps.MaxResonantDependencies)),

        new("spells.maxResonantBytes", ConfigSection.Forge, "Max resonant bytes", "Maximum total bytes of resonant dependency markdown bodies concatenated into the system prompt.", SettingKind.Int, 4096, 1_048_576, 1024, ClampName: nameof(ArcanumSettingClamps.MaxResonantBytes)),

        // ===== Forge — Campaigns =====

        new("campaigns.allowedRoots", ConfigSection.Forge, "Campaigns allowed roots", "Absolute directory roots that campaign registration may use. Empty denies all access by default.", SettingKind.StringArray),

        new("campaigns.maxCampaigns", ConfigSection.Forge, "Max campaigns", "Maximum number of registered campaigns in the Grimoire database.", SettingKind.Int, 10, 10_000, 10, ClampName: nameof(ArcanumSettingClamps.MaxCampaigns)),

        // ===== Forge — Prompts =====

        new("prompts.maxParameterValueChars", ConfigSection.Forge, "Max parameter value chars", "Maximum character length of a single prompt parameter value on render/execute.", SettingKind.Int, 256, 65_536, 256, ClampName: nameof(ArcanumSettingClamps.MaxParameterValueChars)),

        // ===== Forge — Codex =====

        new("codex.maxSizeBytes", ConfigSection.Forge, "Codex max size (bytes)", "Maximum byte size of a CODEX.md read or write. Further capped by Workspaces.MaxFileReadSizeBytes.", SettingKind.Long, 1024, 1_048_576, 1024, ClampName: nameof(ArcanumSettingClamps.CodexMaxSizeBytes)),

        // ===== Proving Grounds =====

        new("provingGrounds.maxInquisitorsPerTrial", ConfigSection.ProvingGrounds, "Max inquisitors per trial", "Maximum Inquisitors allowed on a single Trial.", SettingKind.Int, 1, 200, 1, ClampName: nameof(ArcanumSettingClamps.MaxInquisitorsPerTrial)),

        new("provingGrounds.semanticJudgeMaxTokens", ConfigSection.ProvingGrounds, "Semantic judge max tokens", "Maximum completion tokens for a Semantic Inquisitor FastModel judge call.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.SemanticJudgeMaxTokens)),

        new("provingGrounds.semanticJudgeTimeoutSeconds", ConfigSection.ProvingGrounds, "Semantic judge timeout (s)", "Timeout for a Semantic Inquisitor judge inference call.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.SemanticJudgeTimeoutSeconds)),

        // ===== Cli — core =====

        new("cli.maxAttachFileSizeBytes", ConfigSection.Cli, "Max attach file size (bytes)", "Maximum byte size of a single file attached to an ask/chat request.", SettingKind.Long, 1024, 104_857_600, 1024, ClampName: nameof(ArcanumSettingClamps.MaxAttachFileSizeBytes)),

        new("cli.maxAttachedFilesPerRequest", ConfigSection.Cli, "Max attached files per request", "Maximum files attached to a single ask/chat request.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.MaxAttachedFilesPerRequest)),

        new("cli.maxAttachedFileRelativePathChars", ConfigSection.Cli, "Max attached file path chars", "Maximum character length of an attached file's relative path.", SettingKind.Int, 256, 8192, 1, ClampName: nameof(ArcanumSettingClamps.MaxAttachedFileRelativePathChars)),

        new("cli.theme", ConfigSection.Cli, "Theme", "CLI color theme: Light, Dark, or SystemDefault (follows the OS setting).", SettingKind.Enum, EnumType: typeof(ArcanumTheme)),

        new("cli.showManaBar", ConfigSection.Cli, "Show mana bar", "When true, the chat REPL shows a persistent mana bar (token-budget indicator).", SettingKind.Bool),

        new("cli.doctorHealthTimeoutSeconds", ConfigSection.Cli, "Doctor health timeout (s)", "Timeout for the arcanum doctor API health probe. Increase for slow startups.", SettingKind.Int, 1, 60, 1, ClampName: nameof(ArcanumSettingClamps.DoctorHealthTimeoutSeconds)),

        new("cli.apiRequestTimeoutSeconds", ConfigSection.Cli, "API request timeout (s)", "Timeout for non-streaming CLI API calls (lore, daemon jobs, etc.). Streaming verbs stay unbounded.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.ApiRequestTimeoutSeconds)),

        // ===== Cli — theme colors (Light) =====

        new("cli.themeColors.light.text", ConfigSection.Cli, "Light — text", "Body text color for the Light CLI theme.", SettingKind.Color, Placeholder: "#2A1545"),

        new("cli.themeColors.light.heading", ConfigSection.Cli, "Light — heading", "Heading text color for the Light CLI theme.", SettingKind.Color, Placeholder: "#1E3A8A"),

        new("cli.themeColors.light.highlight", ConfigSection.Cli, "Light — highlight", "Highlight color for the Light CLI theme.", SettingKind.Color, Placeholder: "#008F11"),

        new("cli.themeColors.light.error", ConfigSection.Cli, "Light — error", "Error message color for the Light CLI theme.", SettingKind.Color, Placeholder: "#C41E3A"),

        new("cli.themeColors.light.muted", ConfigSection.Cli, "Light — muted", "Muted/secondary text color for the Light CLI theme.", SettingKind.Color, Placeholder: "#6B5D7A"),

        // ===== Cli — theme colors (Dark) =====

        new("cli.themeColors.dark.text", ConfigSection.Cli, "Dark — text", "Body text color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#E8DCC4"),

        new("cli.themeColors.dark.heading", ConfigSection.Cli, "Dark — heading", "Heading text color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#60A5FA"),

        new("cli.themeColors.dark.highlight", ConfigSection.Cli, "Dark — highlight", "Highlight color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#39FF14"),

        new("cli.themeColors.dark.error", ConfigSection.Cli, "Dark — error", "Error message color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#FF6B6B"),

        new("cli.themeColors.dark.muted", ConfigSection.Cli, "Dark — muted", "Muted/secondary text color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#7A6B90"),

        // ===== Resilience =====

        new("resilience.enabled", ConfigSection.Resilience, "Enabled", "When true, health probing runs and fallback resolution is active. When false (default), behavior is unchanged.", SettingKind.Bool),

        new("resilience.healthProbeIntervalSeconds", ConfigSection.Resilience, "Health probe interval (s)", "Interval between health probes for providers currently considered healthy.", SettingKind.Int, 5, 600, 1, ClampName: nameof(ArcanumSettingClamps.HealthProbeIntervalSeconds)),

        new("resilience.healthRecoveryProbeIntervalSeconds", ConfigSection.Resilience, "Health recovery probe interval (s)", "Slower interval between health probes for providers currently marked unhealthy, to avoid hammering a down provider.", SettingKind.Int, 5, 3_600, 1, ClampName: nameof(ArcanumSettingClamps.HealthRecoveryProbeIntervalSeconds)),

        new("resilience.healthFailureThreshold", ConfigSection.Resilience, "Health failure threshold", "Consecutive failures before a provider is marked Unhealthy and excluded from fallback candidates.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.HealthFailureThreshold)),

        new("resilience.maxFallbackAttempts", ConfigSection.Resilience, "Max fallback attempts", "Maximum number of candidate providers to try per inference turn before giving up.", SettingKind.Int, 1, 10, 1, ClampName: nameof(ArcanumSettingClamps.MaxFallbackAttempts)),

        new("resilience.healthProbeTimeoutSeconds", ConfigSection.Resilience, "Health probe timeout (s)", "HTTP timeout for each individual health probe call.", SettingKind.Int, 1, 30, 1, ClampName: nameof(ArcanumSettingClamps.HealthProbeTimeoutSeconds)),

        // ===== Intelligence — Embeddings & RAG =====
        // RAG Phases 1-5 — The Weave, Divination, session search, semantic codebase retrieval, Saga
        // (long-term associative memory), and embedding-based semantic spell routing. Arcanum:Embeddings
        // is the technical config key (operators search for "embeddings"); the domain metaphor is
        // documented per-row below and in DESIGN.md §21. This list stays in exact sync with
        // ArcanumSettings.Embeddings (see SettingDescriptorCoverageTests).

        new("embeddings.enabled", ConfigSection.Embeddings, "Embeddings enabled", "Master toggle for The Weave (Arcanum's embedding and vector substrate) and Divination (semantic search). When false (default), every RAG code path is unchanged from pre-RAG behavior.", SettingKind.Bool),

        new("embeddings.provider", ConfigSection.Embeddings, "Embeddings provider", "Provider name (from Arcanum:Providers) used to imprint text into The Weave. Required when Enabled is true.", SettingKind.String, Placeholder: "local"),

        new("embeddings.model", ConfigSection.Embeddings, "Embeddings model", "Embedding model name advertised by the configured provider (e.g. nomic-embed-text, text-embedding-3-small). Required when Enabled is true.", SettingKind.String, Placeholder: "nomic-embed-text"),

        new("embeddings.dimensions", ConfigSection.Embeddings, "Embeddings dimensions", "Expected imprinted vector dimension; must match the model's output. Used for the vec0 acceleration table schema. Changing this after data exists requires an operator-triggered re-index.", SettingKind.Int, 64, 4096, 8, ClampName: nameof(ArcanumSettingClamps.EmbeddingsDimensions)),

        new("embeddings.batchSize", ConfigSection.Embeddings, "Embeddings batch size", "Maximum texts imprinted per embedding API call.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.EmbeddingsBatchSize)),

        new("embeddings.chunkSizeChars", ConfigSection.Embeddings, "Embeddings chunk size (chars)", "Maximum characters per chunk when imprinting long documents.", SettingKind.Int, 128, 8192, 64, ClampName: nameof(ArcanumSettingClamps.EmbeddingsChunkSizeChars)),

        new("embeddings.chunkOverlapChars", ConfigSection.Embeddings, "Embeddings chunk overlap (chars)", "Overlap in characters between adjacent chunks; improves Divination at chunk boundaries.", SettingKind.Int, 0, 1024, 16, ClampName: nameof(ArcanumSettingClamps.EmbeddingsChunkOverlapChars)),

        new("embeddings.similarityThreshold", ConfigSection.Embeddings, "Embeddings similarity threshold", "Minimum cosine similarity for a Divination result to be included.", SettingKind.Float, 0, 1, 0.05, ClampName: nameof(ArcanumSettingClamps.EmbeddingsSimilarityThreshold)),

        new("embeddings.maxResults", ConfigSection.Embeddings, "Embeddings max results", "Default maximum results per Divination call. Individual features may override.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.EmbeddingsMaxResults)),

        new("embeddings.requestTimeoutSeconds", ConfigSection.Embeddings, "Embeddings request timeout (s)", "Timeout for a single embedding API call.", SettingKind.Int, 5, 300, 5, ClampName: nameof(ArcanumSettingClamps.EmbeddingsRequestTimeoutSeconds)),

        new("embeddings.maxEmbeddingInputChars", ConfigSection.Embeddings, "Max embedding input (chars)", "Maximum total character count across all inputs in a single POST /v1/embeddings request; exceeding it returns 400 invalid_request_error.", SettingKind.Int, 1000, 10_000_000, 1000, ClampName: nameof(ArcanumSettingClamps.EmbeddingsMaxEmbeddingInputChars)),

        new("embeddings.sessionSearchEnabled", ConfigSection.Embeddings, "Session search enabled", "Phase 2 feature flag: session semantic search (Divination over the Grimoire). Requires Embeddings enabled to also be true.", SettingKind.Bool),

        new("embeddings.embeddingQueueIntervalSeconds", ConfigSection.Embeddings, "Embedding queue interval (s)", "Phase 2: interval between EntryWeavingService embedding queue processing ticks. Only relevant when Session search enabled is true.", SettingKind.Int, 1, 300, 5, ClampName: nameof(ArcanumSettingClamps.EmbeddingsEmbeddingQueueIntervalSeconds)),

        new("embeddings.codebaseRetrievalEnabled", ConfigSection.Embeddings, "Codebase retrieval enabled", "Phase 3 feature flag: semantic codebase retrieval. Requires Embeddings enabled to also be true.", SettingKind.Bool),

        new("embeddings.codebase.maxFilesToIndex", ConfigSection.Embeddings, "Codebase max files to index", "Phase 3: maximum files embedded per workspace during a single indexing tick.", SettingKind.Int, 1, 10_000, 10, ClampName: nameof(ArcanumSettingClamps.EmbeddingsCodebaseMaxFilesToIndex)),

        new("embeddings.codebase.maxFileSizeChars", ConfigSection.Embeddings, "Codebase max file size (chars)", "Phase 3: files larger than this (characters) are skipped during indexing.", SettingKind.Int, 1_000, 500_000, 1_000, ClampName: nameof(ArcanumSettingClamps.EmbeddingsCodebaseMaxFileSizeChars)),

        new("embeddings.codebase.fileExtensions", ConfigSection.Embeddings, "Codebase file extensions", "File extensions eligible for indexing (case-insensitive), e.g. .cs, .py, .md. An empty list indexes nothing.", SettingKind.StringArray),

        new("embeddings.codebase.indexingIntervalMinutes", ConfigSection.Embeddings, "Codebase indexing interval (min)", "Phase 3: background re-indexing interval for workspaces with active inference.", SettingKind.Int, 5, 1_440, 5, ClampName: nameof(ArcanumSettingClamps.EmbeddingsCodebaseIndexingIntervalMinutes)),

        new("embeddings.codebase.maxRetrievedChunks", ConfigSection.Embeddings, "Codebase max retrieved chunks", "Phase 3: maximum file chunks injected into the system prompt per inference turn.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.EmbeddingsCodebaseMaxRetrievedChunks)),

        new("embeddings.sagaEnabled", ConfigSection.Embeddings, "Saga enabled", "Phase 4 feature flag: Saga, Arcanum's long-term associative memory. Requires Embeddings enabled to also be true.", SettingKind.Bool),

        new("embeddings.saga.extractionEnabled", ConfigSection.Embeddings, "Saga extraction enabled", "Phase 4: when Saga enabled is true, controls whether the background SagaExtractionService runs. Set false for retrieval-only mode (existing memories still surface, no new ones are extracted).", SettingKind.Bool),

        new("embeddings.saga.maxMemoriesPerSession", ConfigSection.Embeddings, "Saga max memories per session", "Phase 4: maximum Saga memories associated with a single session. New extractions for a session at this cap are rejected.", SettingKind.Int, 1, 1_000, 1, ClampName: nameof(ArcanumSettingClamps.EmbeddingsSagaMaxMemoriesPerSession)),

        new("embeddings.saga.maxMemoriesTotal", ConfigSection.Embeddings, "Saga max memories total", "Phase 4: maximum total Saga memories across all sessions. New extractions are rejected once this cap is reached.", SettingKind.Int, 100, 1_000_000, 100, ClampName: nameof(ArcanumSettingClamps.EmbeddingsSagaMaxMemoriesTotal)),

        new("embeddings.saga.extractionModel", ConfigSection.Embeddings, "Saga extraction model", "Phase 4: model used for memory extraction. Falls back to Arcanum:FastModel, then Arcanum:DefaultModel, when empty.", SettingKind.String, Placeholder: "(uses FastModel/DefaultModel)"),

        new("embeddings.saga.extractionMaxTokens", ConfigSection.Embeddings, "Saga extraction max tokens", "Phase 4: maximum output tokens for the extraction LLM call.", SettingKind.Int, 100, 4_096, 50, ClampName: nameof(ArcanumSettingClamps.EmbeddingsSagaExtractionMaxTokens)),

        new("embeddings.saga.extractionIntervalMinutes", ConfigSection.Embeddings, "Saga extraction interval (min)", "Phase 4: interval, in minutes, between SagaExtractionService queue processing ticks (informational — the service is event-driven, enqueued after successful inference turns, not polling).", SettingKind.Int, 1, 1_440, 5, ClampName: nameof(ArcanumSettingClamps.EmbeddingsSagaExtractionIntervalMinutes)),

        new("embeddings.saga.extractionWindowEntries", ConfigSection.Embeddings, "Saga extraction window (entries)", "Phase 4: number of recent Grimoire entries reviewed per extraction call.", SettingKind.Int, 2, 50, 1, ClampName: nameof(ArcanumSettingClamps.EmbeddingsSagaExtractionWindowEntries)),

        new("embeddings.semanticSpellRoutingEnabled", ConfigSection.Embeddings, "Semantic Spell Routing enabled", "Phase 5 feature flag: embedding-based Spell Routing pre-filter. When false (default), the existing LLM-based SemanticRouter is used unchanged. Requires Embeddings enabled to also be true.", SettingKind.Bool),

        new("embeddings.spellRoutingHybridMode", ConfigSection.Embeddings, "Spell Routing hybrid mode", "Phase 5: when true and Semantic Spell Routing enabled is also true, embedding similarity pre-filters the spell catalog to the top-K candidates before the LLM router picks from that reduced set. When false, the highest-similarity spell above the similarity threshold wins outright with no LLM call.", SettingKind.Bool),

        new("embeddings.spellRoutingHybridTopK", ConfigSection.Embeddings, "Spell Routing hybrid top-K", "Phase 5: number of top candidates passed to the LLM router in hybrid mode.", SettingKind.Int, 1, 20, 1, ClampName: nameof(ArcanumSettingClamps.EmbeddingsSpellRoutingHybridTopK)),

        // ===== Scrying (Vision/Multimodality) =====

        new("scrying.enabled", ConfigSection.Scrying, "Scrying enabled", "Master kill-switch. When false, image content is rejected at the API boundary even for vision-capable models.", SettingKind.Bool),

        new("scrying.maxImageBytes", ConfigSection.Scrying, "Max image bytes", "Maximum bytes per image, measured against the decoded data: URI payload. http(s)-hosted images are not size-checked here; the downstream provider fetches and rejects them.", SettingKind.Long, 1024, 20_971_520, 1024, ClampName: nameof(ArcanumSettingClamps.ScryingMaxImageBytes)),

        new("scrying.maxImagesPerRequest", ConfigSection.Scrying, "Max images per request", "Maximum images per inference request (native Scrying foci and /v1 image_url parts combined).", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.ScryingMaxImagesPerRequest)),

        new("scrying.allowedMimeTypes", ConfigSection.Scrying, "Allowed MIME types", "Allowed image MIME types. Non-matching types are rejected. Only enforced for data: URI images; not enforced for http(s) URLs.", SettingKind.StringArray),

        // ===== Pricing =====

        new("pricing.defaultPricing.inputPer1M", ConfigSection.Pricing, "Default input price (USD / 1M tokens)", "Fallback cost per 1M input tokens when a model has no explicit pricing entry. Default free.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingInputPer1M)),

        new("pricing.defaultPricing.outputPer1M", ConfigSection.Pricing, "Default output price (USD / 1M tokens)", "Fallback cost per 1M output tokens when a model has no explicit pricing entry. Default free.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingOutputPer1M)),

        new("pricing.defaultPricing.reasoningPer1M", ConfigSection.Pricing, "Default reasoning price (USD / 1M tokens)", "Optional fallback cost per 1M reasoning tokens. Reasoning is included in output token counts; when unset, the output price applies.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingOutputPer1M), AllowUnset: true),

        new("pricing.defaultPricing.cachedPer1M", ConfigSection.Pricing, "Default cached input price (USD / 1M tokens)", "Fallback cost per 1M cached input tokens. Default 0.00 — set explicitly; cached tokens are not assumed free forever.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingInputPer1M)),

        new("pricing.modelPricing", ConfigSection.Pricing, "Per-model pricing", "Dictionary keyed by model name. Each entry supplies input, output, optional reasoning, and cached USD cost per 1M tokens.", SettingKind.Dictionary),

        // ===== Budget =====

        new("budget.enabled", ConfigSection.Budget, "Budget enforcement enabled", "When true, daily USD spend is checked against budget.limitUsd and inference is rejected with 429 once the limit is reached.", SettingKind.Bool),

        new("budget.dailyLimitUsd", ConfigSection.Budget, "Daily limit (USD)", "Maximum USD spend allowed per UTC day before inference is rejected with Budget.Exceeded (HTTP 429).", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.BudgetDailyLimitUsd)),

        new("budget.alertThresholdPercent", ConfigSection.Budget, "Alert threshold (%)", "Percentage of the daily limit at which a Comm Link warning is dispatched. Default 80; clamped 1-100.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.BudgetAlertThresholdPercent)),

        // ===== Web Browsing =====

        new("webBrowsing.enabled", ConfigSection.WebBrowsing, "Web browsing enabled", "When true, the browse_web built-in tool is advertised and can fetch external URLs subject to the outbound SSRF guard and Sanctum network policy.", SettingKind.Bool),

        new("webBrowsing.maxContentBytes", ConfigSection.WebBrowsing, "Max browsed content (bytes)", "Maximum response body bytes read from a fetched page. Content beyond this is truncated with a marker. Default 50,000; clamped 1,000 - 1,000,000.", SettingKind.Int, 1_000, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.WebBrowsingMaxContentBytes)),

        new("webBrowsing.requestTimeoutSeconds", ConfigSection.WebBrowsing, "Web browsing timeout (s)", "Wall-clock timeout for the outbound HTTP request made by browse_web. Default 10; clamped 1 - 60.", SettingKind.Int, 1, 60, 1, ClampName: nameof(ArcanumSettingClamps.WebBrowsingRequestTimeoutSeconds)),

        new("webBrowsing.maxLinks", ConfigSection.WebBrowsing, "Max browsed links", "Maximum number of absolute links returned by browse_web. Default 10; clamped 0 - 100.", SettingKind.Int, 0, 100, 1, ClampName: nameof(ArcanumSettingClamps.WebBrowsingMaxLinks)),

        // ===== Client Tool Forwarding =====

        new("clientToolForwarding.enabled", ConfigSection.ClientToolForwarding, "Client tool forwarding enabled", "When true, client-supplied tools and tool_choice on /v1/chat/completions are forwarded to the resolved provider instead of being rejected. Arcanum does not execute the tools; the client must round-trip the tool_calls response.", SettingKind.Bool),

        new("clientToolForwarding.maxClientTools", ConfigSection.ClientToolForwarding, "Max client tools", "Maximum number of client-supplied tools accepted per /v1/chat/completions request. Default 20; clamped 1 - 100.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.ClientToolForwardingMaxClientTools)),

        // ===== Content Guardrails =====

        new("guardrails.enabled", ConfigSection.Guardrails, "Content guardrails enabled", "When true, input and output are scanned by the GuardrailsPipeline. PII in input is rejected with Guardrails.PiiDetected; toxicity/topic violations are rejected with Guardrails.Blocked. Default false — a complete pass-through until an operator opts in.", SettingKind.Bool),

        new("guardrails.detectPii", ConfigSection.Guardrails, "Detect PII", "When true (default), email/phone/SSN/credit-card patterns in input messages are detected and the turn is rejected before inference runs.", SettingKind.Bool),

        new("guardrails.blockToxicity", ConfigSection.Guardrails, "Block toxicity", "When true, input or output containing any ToxicityBlocklist keyword is rejected. Default false — an empty blocklist is a no-op even when this is true.", SettingKind.Bool),

        new("guardrails.toxicityBlocklist", ConfigSection.Guardrails, "Toxicity blocklist", "Substring (case-insensitive) blocklist matched against input and output text. Only consulted when BlockToxicity is true. Default empty.", SettingKind.StringArray),

        new("guardrails.allowedTopics", ConfigSection.Guardrails, "Allowed topics", "Optional allow-list of regex patterns. When non-empty, input that fails to match any pattern is rejected. Default empty — all topics allowed.", SettingKind.StringArray),

        new("guardrails.blockedTopics", ConfigSection.Guardrails, "Blocked topics", "Optional block-list of regex patterns. Input or output matching any pattern is rejected. Default empty — no topics blocked.", SettingKind.StringArray),

        new("guardrails.streamingMode", ConfigSection.Guardrails, "Streaming output-filter mode", "buffered (default when guardrails are enabled) holds tokens until the output filter passes. passthrough emits tokens live and filters post-hoc (toxic text may reach the client). Explicit passthrough is honored with a warning. Filters assistant output only — not tool side effects. No-op when Guardrails:Enabled is false.", SettingKind.Enum, EnumType: typeof(GuardrailsStreamingMode)),

        // ===== Content Guardrails — Audit log =====

        new("guardrails.auditLog.enabled", ConfigSection.Guardrails, "Guardrails audit log enabled", "Master toggle for the persisted guardrails audit log. When false (default), no violation file I/O occurs and GET /api/guardrails/audit returns an empty list. Ineffective when Guardrails:Enabled is false.", SettingKind.Bool),

        new("guardrails.auditLog.filePath", ConfigSection.Guardrails, "Guardrails audit log file path", "Base path; the directory is where dated guardrails-YYYYMMDD.jsonl files are written (one per UTC day).", SettingKind.Path, Placeholder: "~/.config/arcanum/guardrails.jsonl"),

        new("guardrails.auditLog.maxSizeMb", ConfigSection.Guardrails, "Guardrails audit log max size (MB)", "Soft per-day-file size cap; further writes for that day are dropped once reached. Default 100; clamped 10-1,000.", SettingKind.Int, 10, 1_000, 10, ClampName: nameof(ArcanumSettingClamps.HostAuditLogMaxSizeMb)),

        new("guardrails.auditLog.retentionDays", ConfigSection.Guardrails, "Guardrails audit log retention (days)", "Dated log files older than this are deleted automatically. Default 7; clamped 1-365.", SettingKind.Int, 1, 365, 1, ClampName: nameof(ArcanumSettingClamps.HostAuditLogRetentionDays)),

    ];

    private static SettingDescriptor[] CreateTokenizationDescriptors(string prefix, string group) =>
    [
        new($"{prefix}.type", ConfigSection.Providers, "Tokenization profile type", "Exact local tokenizer, provider tokenizer API, calibrated approximation, or conservative unknown-model fallback.", SettingKind.Enum, EnumType: typeof(ModelTokenizationProfileType), Group: group),
        new($"{prefix}.tokenizerId", ConfigSection.Providers, "Tokenizer identifier", "Tokenizer or estimator identifier, such as o200k_base. Required for exact local tokenizers.", SettingKind.String, Placeholder: "o200k_base", Group: group, AllowUnset: true),
        new($"{prefix}.safetyMarginPercent", ConfigSection.Providers, "Safety margin (%)", "Optional percentage added to estimated input. Ignored for exact local tokenizers.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.EstimatedTokenSafetyMarginPercent), Group: group, AllowUnset: true),
        new($"{prefix}.perMessageOverheadTokens", ConfigSection.Providers, "Per-message framing tokens", "Optional provider chat-template framing added for each message.", SettingKind.Int, 0, 32, 1, ClampName: nameof(ArcanumSettingClamps.PerMessageTemplateOverheadTokens), Group: group, AllowUnset: true),
        new($"{prefix}.perToolOverheadTokens", ConfigSection.Providers, "Per-tool framing tokens", "Optional provider function/tool framing added for each declared tool.", SettingKind.Int, 0, 128, 1, ClampName: nameof(ArcanumSettingClamps.TokenizationPerToolOverheadTokens), Group: group, AllowUnset: true),
        new($"{prefix}.providerFramingTokens", ConfigSection.Providers, "Provider framing tokens", "Optional once-per-call provider priming/framing reserve.", SettingKind.Int, 0, 1024, 1, ClampName: nameof(ArcanumSettingClamps.TokenizationProviderFramingTokens), Group: group, AllowUnset: true),
        new($"{prefix}.stopTokenOverheadTokens", ConfigSection.Providers, "Stop-token overhead", "Optional once-per-call provider stop/end-marker reserve.", SettingKind.Int, 0, 128, 1, ClampName: nameof(ArcanumSettingClamps.TokenizationStopTokenOverheadTokens), Group: group, AllowUnset: true),
        new($"{prefix}.unknownImageReserveTokens", ConfigSection.Providers, "Unknown image reserve", "Optional conservative per-image reserve when no provider image formula is available.", SettingKind.Int, 1, 128_000, 1, ClampName: nameof(ArcanumSettingClamps.UnknownImageTokenReserve), Group: group, AllowUnset: true),
        new($"{prefix}.confidence", ConfigSection.Providers, "Estimate confidence", "Optional calibrated confidence from 0 through 1. Exact local tokenizers resolve to confidence 1.", SettingKind.Float, 0, 1, 0.05, ClampName: nameof(ArcanumSettingClamps.TokenizationConfidence), Group: group, AllowUnset: true),
    ];

    private static SettingDescriptor[] CreatePromptCachingDescriptors(string prefix, string group) =>
    [
        new($"{prefix}.controlMode", ConfigSection.Providers, "Prompt-cache control mode", "providerManaged sends no Arcanum directives, explicit emits only the configured verified contract, and none emits no directives while classifying calls as non-cacheable.", SettingKind.Enum, EnumType: typeof(PromptCachingControlMode), Group: group),
        new($"{prefix}.wireDialect", ConfigSection.Providers, "Prompt-cache wire dialect", "Fixed code-owned request contract. Never inferred from provider or model names; enabling one asserts that the target endpoint accepts it.", SettingKind.Enum, EnumType: typeof(PromptCachingWireDialect), Group: group),
        new($"{prefix}.cacheKeysSupported", ConfigSection.Providers, "Supports cache keys", "Declares that the configured provider/model accepts a provider cache routing key.", SettingKind.Bool, Group: group),
        new($"{prefix}.emitCacheKey", ConfigSection.Providers, "Emit cache key", "Emits a privacy-safe opaque stable-prefix key when explicit mode and cache-key support are enabled.", SettingKind.Bool, Group: group),
        new($"{prefix}.retentionSelectionSupported", ConfigSection.Providers, "Supports retention selection", "Declares that the configured provider/model accepts the selected prompt-cache retention policy.", SettingKind.Bool, Group: group),
        new($"{prefix}.retention", ConfigSection.Providers, "Prompt-cache retention", "Provider default, in-memory, 24-hour, or reserved 30-minute policy. Validation restricts values to the selected verified dialect.", SettingKind.Enum, EnumType: typeof(PromptCacheRetentionPolicy), Group: group),
        new($"{prefix}.stablePrefixBreakpointsSupported", ConfigSection.Providers, "Supports stable-prefix breakpoints", "Declares support for explicit cumulative-prefix content breakpoints. The pinned adapter currently rejects unverified breakpoint dialects.", SettingKind.Bool, Group: group),
        new($"{prefix}.emitStablePrefixBreakpoint", ConfigSection.Providers, "Emit stable-prefix breakpoint", "Marks the verified contiguous stable prefix when the selected dialect supports explicit breakpoints.", SettingKind.Bool, Group: group),
        new($"{prefix}.toolSchemasParticipate", ConfigSection.Providers, "Tool schemas participate", "Includes the finalized deterministic tool definitions in prompt-cache key planning when the provider contract caches tools.", SettingKind.Bool, Group: group),
        new($"{prefix}.reportsCachedInputUsage", ConfigSection.Providers, "Reports cached input usage", "Declares that provider usage reports cached input tokens. Accounting still records any usage the provider actually returns.", SettingKind.Bool, Group: group),
    ];

    public static IReadOnlyDictionary<ConfigSection, IReadOnlyList<SettingDescriptor>> BySection { get; } =
        All.GroupBy(static d => d.Section)

            .ToDictionary(static g => g.Key, static g => (IReadOnlyList<SettingDescriptor>)g.ToList());

    public static SettingDescriptor? Find(string key) => All.FirstOrDefault(d => d.Key == key);

}
