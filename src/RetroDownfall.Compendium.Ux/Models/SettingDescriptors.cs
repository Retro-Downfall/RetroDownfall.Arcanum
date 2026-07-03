using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Compendium.Ux.Models;

public static class SettingDescriptors
{

    public static IReadOnlyList<SettingDescriptor> All { get; } =
    [

        // ===== Host =====

        new("host.port", ConfigSection.Host, "Port", "Kestrel listen port.", SettingKind.Int, 1, 65535, 1, ClampName: nameof(ArcanumSettingClamps.HostPort)),

        new("host.retainedLogFileCount", ConfigSection.Host, "Retained log file count", "Number of rolling log files kept on disk before oldest are pruned.", SettingKind.Int, 1, 366, 1, ClampName: nameof(ArcanumSettingClamps.RetainedLogFileCount)),

        new("host.enableEnterpriseTelemetry", ConfigSection.Host, "Enable enterprise telemetry", "When true, emits structured telemetry events to configured sinks.", SettingKind.Bool),

        new("host.corsAllowedOrigins", ConfigSection.Host, "CORS allowed origins", "Allowed origins for CORS. Use [\"*\"] to allow any origin (browser callers can read responses with the API key).", SettingKind.StringArray),

        new("host.enableScalarUi", ConfigSection.Host, "Enable Scalar UI", "When true, mounts the Scalar interactive API documentation UI at /api/scalar. The UI ships with inline JavaScript and CSS that conflict with strict CSP; default false.", SettingKind.Bool),

        new("host.systemFingerprint", ConfigSection.Host, "System fingerprint", "Optional stable identifier surfaced as system_fingerprint on OpenAI-shaped /v1/chat/completions responses. When null, the API derives one from the assembly version.", SettingKind.String, Placeholder: "arcanum-0.1.0-beta"),

        new("host.listenAny", ConfigSection.Host, "Listen on all interfaces", "When true, Kestrel binds to all network interfaces instead of loopback. The ARCANUM_HOST_ANY env var is still honored as an override.", SettingKind.Bool),

        new("host.maxRequestBodyBytes", ConfigSection.Host, "Max request body bytes", "Kestrel MaxRequestBodySize in bytes. Default 10 MiB; clamped 256 KiB - 1 GiB.", SettingKind.Long, 262144, 1073741824, 1048576, ClampName: nameof(ArcanumSettingClamps.MaxRequestBodyBytes)),

        new("host.workspace", ConfigSection.Host, "Default workspace path", "Optional default workspace root for spell management and other workspace-scoped API routes. Prefer absolute paths.", SettingKind.Path, Placeholder: "/home/me/projects"),

        new("host.rateLimit.enabled", ConfigSection.Host, "Rate limit enabled", "When true, applies a fixed-window limiter to /api and /v1 endpoint groups, partitioned by API key (or IP when no key header is present).", SettingKind.Bool),

        new("host.rateLimit.permitLimit", ConfigSection.Host, "Rate limit permit limit", "Maximum requests permitted per window per partition. Default 120.", SettingKind.Int, 1, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.RateLimitPermitLimit)),

        new("host.rateLimit.windowSeconds", ConfigSection.Host, "Rate limit window seconds", "Window size in seconds for the fixed-window limiter. Default 60.", SettingKind.Int, 1, 86_400, 1, ClampName: nameof(ArcanumSettingClamps.RateLimitWindowSeconds)),

        new("host.rateLimit.queueLimit", ConfigSection.Host, "Rate limit queue limit", "Maximum queued requests per partition (served once the window resets). Default 0: excess requests are rejected with HTTP 429.", SettingKind.Int, 0, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.RateLimitQueueLimit)),

        // ===== Server =====

        new("server.pidFilePath", ConfigSection.Server, "PID file path", "Path to the arcanum.pid file written by `arcanum serve`. Defaults to ~/.config/arcanum/arcanum.pid.", SettingKind.Path, Placeholder: "~/.config/arcanum/arcanum.pid"),

        // ===== Providers (top-level + per-provider rows) =====

        new("defaultModel", ConfigSection.Providers, "Default model", "Model used when a request does not specify one. Must match a models entry on some provider.", SettingKind.String, Placeholder: "deepseek-chat"),

        new("fastModel", ConfigSection.Providers, "Fast model", "Model used for internal background summarization, semantic routing preflight, and semantic Inquisitor judges. Must match a models entry on some provider.", SettingKind.String, Placeholder: "mistral:latest"),

        new("providers.name", ConfigSection.Providers, "Provider name", "Human-readable name for this provider entry.", SettingKind.String, Placeholder: "Local Ollama"),

        new("providers.type", ConfigSection.Providers, "Provider type", "Backend kind: Ollama (local OllamaSharp), OpenAICompatible (any OpenAI-shaped HTTP API), or LlamaCppServer (spawned local llama-server).", SettingKind.Enum, EnumType: typeof(AiProviderKind)),

        new("providers.endpoint", ConfigSection.Providers, "Endpoint", "Base URL for the provider API. Ollama: http://localhost:11434. OpenAI-compatible usually includes /v1. Ignored for LlamaCppServer.", SettingKind.String, Placeholder: "https://api.openai.com/v1"),

        new("providers.apiKey", ConfigSection.Providers, "API key", "Secret API key for this provider. Encrypted at rest with dp:v1: prefix; decrypted in the UI for editing. Leave blank for local Ollama.", SettingKind.Secret, Placeholder: "sk-..."),

        new("providers.models", ConfigSection.Providers, "Models", "Model IDs advertised by this provider. Must include the DefaultModel and FastModel if those reference this provider.", SettingKind.StringArray),

        new("providers.contextWindowLimit", ConfigSection.Providers, "Context window limit", "Maximum tokens the hub will assemble into a single inference request for this provider. Clamp 256 - 2,097,152.", SettingKind.Int, 256, 2_097_152, 128, ClampName: nameof(ArcanumSettingClamps.ContextWindowLimit)),

        new("providers.llamaCpp.modelMap", ConfigSection.Providers, "LlamaCpp model map", "Maps model keys to remote http/https URLs for on-demand GGUF download when the model is not yet cached. Only used when type is LlamaCppServer.", SettingKind.Dictionary, Placeholder: "phi3=https://example.com/phi3.gguf"),

        // ===== Intelligence =====

        new("intelligence.executeCommandTimeoutSeconds", ConfigSection.Intelligence, "Execute command timeout (s)", "Wall-clock timeout for in-process MCP execute_command and run_spell_script. Must be <= Mcp.RequestTimeoutSeconds.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.ExecuteCommandTimeoutSeconds)),

        new("intelligence.semanticRouterPreflightTimeoutSeconds", ConfigSection.Intelligence, "Semantic router preflight timeout (s)", "Timeout for the FastModel preflight call that decides whether semantic spell routing applies.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.SemanticRouterPreflightTimeoutSeconds)),

        new("intelligence.semanticRouterMaxTokens", ConfigSection.Intelligence, "Semantic router max tokens", "Maximum completion tokens for the semantic router preflight call.", SettingKind.Int, 1, 4096, 1, ClampName: nameof(ArcanumSettingClamps.SemanticRouterMaxTokens)),

        new("intelligence.semanticRouterTemperature", ConfigSection.Intelligence, "Semantic router temperature", "Sampling temperature for the semantic router preflight call.", SettingKind.Float, 0, 2, 0.1, ClampName: nameof(ArcanumSettingClamps.SemanticRouterTemperature)),

        new("intelligence.listDirectoryMaxPaths", ConfigSection.Intelligence, "List directory max paths", "Maximum file/dir entries returned by the list_directory tool in one call.", SettingKind.Int, 1, 2000, 1, ClampName: nameof(ArcanumSettingClamps.ListDirectoryMaxPaths)),

        new("intelligence.enableLoreSystem", ConfigSection.Intelligence, "Enable lore system", "When true, the operator key-value Lore memory is consulted during inference.", SettingKind.Bool),

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

        new("intelligence.maxToolInferenceRounds", ConfigSection.Intelligence, "Max tool inference rounds", "Maximum agentic tool rounds per inference turn. Beyond this the turn fails with Hub.ToolLoop.", SettingKind.Int, 1, 64, 1, ClampName: nameof(ArcanumSettingClamps.MaxToolInferenceRounds)),

        new("intelligence.compressionPreflightMinMessages", ConfigSection.Intelligence, "Compression preflight min messages", "Minimum assembled-message count before context-compression preflight runs. Short threads skip tokenizer cost.", SettingKind.Int, 0, 100, 1, ClampName: nameof(ArcanumSettingClamps.CompressionPreflightMinMessages)),

        new("intelligence.perMessageTemplateOverheadTokens", ConfigSection.Intelligence, "Per-message template overhead (tokens)", "Tokens added to the pre-flight count to approximate chat-template framing (role markers, separators).", SettingKind.Int, 0, 32, 1, ClampName: nameof(ArcanumSettingClamps.PerMessageTemplateOverheadTokens)),

        new("intelligence.tokenizerEncoding", ConfigSection.Intelligence, "Tokenizer encoding", "Tiktoken encoding name used by InferenceTokenizerResolver. Only change if validating counts against a non-OpenAI model family with a different encoding.", SettingKind.String, Placeholder: "o200k_base"),

        new("intelligence.maxOpenApiMessages", ConfigSection.Intelligence, "Max OpenAI API messages", "Maximum messages accepted in a single /v1/chat/completions request body.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.MaxOpenApiMessages)),

        new("intelligence.maxStatelessMessages", ConfigSection.Intelligence, "Max stateless messages", "Maximum messages accepted in a single stateless /api/intelligence/ping request body.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.MaxStatelessMessages)),

        new("intelligence.maxContentPartsPerMessage", ConfigSection.Intelligence, "Max content parts per message", "Maximum content parts accepted within a single multimodal message.", SettingKind.Int, 1, 1024, 1, ClampName: nameof(ArcanumSettingClamps.MaxContentPartsPerMessage)),

        new("intelligence.maxPingPromptChars", ConfigSection.Intelligence, "Max ping prompt chars", "Maximum character length of the prompt in a single /api/intelligence/ping request.", SettingKind.Int, 1, 262_144, 1024, ClampName: nameof(ArcanumSettingClamps.MaxPingPromptChars)),

        new("intelligence.maxPlanSteps", ConfigSection.Intelligence, "Max plan steps", "Maximum steps in an Apprentice plan before the plan is rejected.", SettingKind.Int, 1, 200, 1, ClampName: nameof(ArcanumSettingClamps.MaxPlanSteps)),

        new("intelligence.inferenceTimeoutSeconds", ConfigSection.Intelligence, "Inference timeout (s)", "Wall-clock timeout for a single inference turn (buffered or streaming), including tool rounds. Default 600.", SettingKind.Int, 5, 3600, 1, ClampName: nameof(ArcanumSettingClamps.InferenceTimeoutSeconds)),

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

        // ===== LlamaCpp =====

        new("llamaCpp.serverExecutablePath", ConfigSection.LlamaCpp, "Server executable path", "Absolute or relative path to the llama-server executable. When null, search PATH (and llama-server.exe on Windows).", SettingKind.Path, Placeholder: "/usr/local/bin/llama-server"),

        new("llamaCpp.gpuLayers", ConfigSection.LlamaCpp, "GPU layers", "GPU layers to offload. 0 = CPU only. -1 = sentinel for offload all (mapped to 999 on the command line).", SettingKind.Int, -1, 1024, 1, ClampName: nameof(ArcanumSettingClamps.LlamaGpuLayers)),

        new("llamaCpp.contextSize", ConfigSection.LlamaCpp, "Context size", "Context size passed as --ctx-size.", SettingKind.Int, 256, 1_048_576, 256, ClampName: nameof(ArcanumSettingClamps.LlamaContextSize)),

        new("llamaCpp.portStart", ConfigSection.LlamaCpp, "Port start", "First port to try when auto-selecting a listen port for a spawned llama-server.", SettingKind.Int, 1, 65_535, 1, ClampName: nameof(ArcanumSettingClamps.LlamaPortStart)),

        new("llamaCpp.portRange", ConfigSection.LlamaCpp, "Port range", "Number of consecutive ports to try from PortStart. PortStart + PortRange - 1 must not exceed 65535.", SettingKind.Int, 1, 65_535, 1, ClampName: nameof(ArcanumSettingClamps.LlamaPortRange)),

        new("llamaCpp.maxConcurrentRequests", ConfigSection.LlamaCpp, "Max concurrent requests", "Maximum concurrent inference requests per running llama-server.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.LlamaMaxConcurrentRequests)),

        new("llamaCpp.healthProbeTimeoutSeconds", ConfigSection.LlamaCpp, "Health probe timeout (s)", "Timeout for GET /health probes during server startup.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.LlamaHealthProbeTimeoutSeconds)),

        new("llamaCpp.startTimeoutSeconds", ConfigSection.LlamaCpp, "Start timeout (s)", "Maximum wait for a spawned server to become healthy.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.LlamaStartTimeoutSeconds)),

        new("llamaCpp.shutdownTimeoutSeconds", ConfigSection.LlamaCpp, "Shutdown timeout (s)", "Grace period before Kill(entireProcessTree: true) on shutdown.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.LlamaShutdownTimeoutSeconds)),

        new("llamaCpp.additionalArguments", ConfigSection.LlamaCpp, "Additional arguments", "Extra arguments appended to the llama-server command line (one per line/item).", SettingKind.StringArray),

        new("llamaCpp.maxCachedModels", ConfigSection.LlamaCpp, "Max cached models", "Maximum cached GGUF entries before LRU eviction.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.LlamaMaxCachedModels)),

        new("llamaCpp.modelDownloadTimeoutSeconds", ConfigSection.LlamaCpp, "Model download timeout (s)", "Timeout for the named HttpClient(LlamaModelDownload) used to fetch GGUF files.", SettingKind.Int, 60, 86_400, 60, ClampName: nameof(ArcanumSettingClamps.LlamaModelDownloadTimeoutSeconds)),

        new("llamaCpp.modelDownloadMaxBytes", ConfigSection.LlamaCpp, "Model download max (bytes)", "Maximum bytes accepted for a single GGUF download. Default 50 GiB.", SettingKind.Long, 1_048_576, 214_748_364_800, 1_073_741_824, ClampName: nameof(ArcanumSettingClamps.LlamaModelDownloadMaxBytes)),

        new("llamaCpp.modelSha256Map", ConfigSection.LlamaCpp, "Model SHA-256 map", "Optional lowercase SHA-256 hex digests keyed by model cache key for download verification.", SettingKind.Dictionary, Placeholder: "phi3=abcdef..."),

        new("llamaCpp.requireModelHash", ConfigSection.LlamaCpp, "Require model hash", "When true (default), GGUF downloads without a matching SHA-256 are rejected. Set false to allow unverified pulls.", SettingKind.Bool),

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

        // ===== Security — Ward =====

        new("ward.enabled", ConfigSection.Security, "Wards enabled", "Master toggle for the Forbidden Arts approval gate.", SettingKind.Bool),

        new("ward.forbiddenArts", ConfigSection.Security, "Forbidden arts", "Tool names gated by the Ward approval flow before execution.", SettingKind.StringArray),

        new("ward.timeoutSeconds", ConfigSection.Security, "Ward timeout (s)", "Seconds before a pending Ward approval auto-expires.", SettingKind.Int, 10, 600, 1, ClampName: nameof(ArcanumSettingClamps.WardTimeoutSeconds)),

        new("ward.maxActiveWards", ConfigSection.Security, "Max active wards", "Maximum simultaneously pending Ward approvals.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxActiveWards)),

        new("ward.autoDenyInUnattendedMode", ConfigSection.Security, "Auto-deny in unattended mode", "When true and the host is unattended, Ward approvals are auto-denied instead of hanging.", SettingKind.Bool),

        // ===== Security — API key =====

        new("security.maxApiKeyHeaderUtf16Chars", ConfigSection.Security, "Max API key header chars", "Maximum UTF-16 char length accepted in the X-Arcanum-Key / Authorization header.", SettingKind.Int, 128, 8192, 16, ClampName: nameof(ArcanumSettingClamps.MaxApiKeyHeaderUtf16Chars)),

        new("security.apiKeyCacheTtlSeconds", ConfigSection.Security, "API key cache TTL (s)", "TTL for the in-memory cache of the expected API key digest. After this window, on-disk rotation takes effect without a restart.", SettingKind.Int, 1, 3600, 1, ClampName: nameof(ArcanumSettingClamps.ApiKeyCacheTtlSeconds)),

        // ===== CommLink =====

        new("commLink.webhookUrl", ConfigSection.CommLink, "Webhook URL", "Outbound URL POSTed to on Comm Link alerts. Defaults to https; add http to AllowedSchemes to use plaintext.", SettingKind.Secret, Placeholder: "https://hooks.example.com/arcanum"),

        new("commLink.webhookTimeoutSeconds", ConfigSection.CommLink, "Webhook timeout (s)", "Timeout for the named HttpClient(CommLinkWebhook) used to POST alerts.", SettingKind.Int, 1, 120, 1, ClampName: nameof(ArcanumSettingClamps.WebhookTimeoutSeconds)),

        new("commLink.allowedSchemes", ConfigSection.CommLink, "Allowed schemes", "URI schemes the webhook dispatcher is permitted to call. Default [https]. Add http to opt in to plaintext.", SettingKind.StringArray),

        new("commLink.allowedHosts", ConfigSection.CommLink, "Allowed hosts", "Optional allowed webhook hosts (e.g. hooks.example.com). When populated, any URL whose host is not listed is rejected at startup.", SettingKind.StringArray),

        // ===== Storage — Grimoire =====

        new("grimoire.maxMessagesPerConversationLoad", ConfigSection.Storage, "Max messages per conversation load", "Maximum entries loaded into memory for a single GetSessionAsync hydration; bounds RAM on long threads.", SettingKind.Int, 50, 5000, 1, ClampName: nameof(ArcanumSettingClamps.MaxMessagesPerConversationLoad)),

        new("grimoire.workspaceContextRetentionCount", ConfigSection.Storage, "Workspace context retention", "Number of Chronosync WorkspaceContext snapshots retained per workspace path before older rows are purged.", SettingKind.Int, 1, 1000, 1, ClampName: nameof(ArcanumSettingClamps.WorkspaceContextRetentionCount)),

        new("grimoire.defaultLoreListLimit", ConfigSection.Storage, "Default lore list limit", "Default page size for GET /api/lore when limit is omitted.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.ListQueryLimit)),

        // ===== Storage — Sessions =====

        new("sessions.defaultQueryLimit", ConfigSection.Storage, "Default query limit", "Default page size for session entry queries when limit is omitted.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.SessionQueryLimit)),

        new("sessions.maxStreamReplayEntries", ConfigSection.Storage, "Max stream replay entries", "Maximum entries replayed to a newly connected session SSE subscriber.", SettingKind.Int, 1, 10_000, 1, ClampName: nameof(ArcanumSettingClamps.SessionStreamReplayLimit)),

        new("sessions.maxEntriesPerSession", ConfigSection.Storage, "Max entries per session", "Maximum entries allowed in a single session before inserts are rejected.", SettingKind.Int, 100, 1_000_000, 1, ClampName: nameof(ArcanumSettingClamps.MaxEntriesPerSession)),

        new("sessions.maxEntryContentBytes", ConfigSection.Storage, "Max entry content (bytes)", "Maximum byte size of a single session entry's content.", SettingKind.Int, 1024, 16_777_216, 1024, ClampName: nameof(ArcanumSettingClamps.MaxEntryContentBytes)),

        // ===== Storage — EventBus =====

        new("eventBus.channelCapacity", ConfigSection.Storage, "Channel capacity", "Per-subscriber bounded channel capacity for live SSE push updates. When full, DropOldest discards the oldest frame.", SettingKind.Int, 64, 65_536, 1, ClampName: nameof(ArcanumSettingClamps.EventBusChannelCapacity)),

        new("eventBus.heartbeatSeconds", ConfigSection.Storage, "Heartbeat (s)", "SSE keep-alive comment interval for /api/events/*, session stream, and Chronicle. 0 disables heartbeats.", SettingKind.Int, 0, 300, 1, ClampName: nameof(ArcanumSettingClamps.EventBusHeartbeatSeconds)),

        new("eventBus.maxSseConnections", ConfigSection.Storage, "Max SSE connections", "Maximum concurrent SSE connections across all event streams; excess requests get 503 Api.TooManyConnections.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxSseConnections)),

        // ===== Storage — Logs =====

        new("logs.ringBufferCapacity", ConfigSection.Storage, "Ring buffer capacity", "Capacity of the in-memory log ring buffer. Read once at construction; changes require a restart.", SettingKind.Int, 1000, 100_000, 1000, ClampName: nameof(ArcanumSettingClamps.LogRingBufferCapacity)),

        new("logs.minLevelInBuffer", ConfigSection.Storage, "Min level in buffer", "Minimum log level retained in the in-memory ring buffer.", SettingKind.Enum, EnumType: typeof(LogLevel)),

        // ===== Storage — Workspaces =====

        new("workspaces.maxFileReadSizeBytes", ConfigSection.Storage, "Max file read (bytes)", "Maximum byte size of a single workspace file read via the API.", SettingKind.Long, 1024, 10_485_760, 1024, ClampName: nameof(ArcanumSettingClamps.MaxFileReadSizeBytes)),

        new("workspaces.listDirectoryMaxDepth", ConfigSection.Storage, "List directory max depth", "Maximum directory depth for recursive workspace file listing.", SettingKind.Int, 1, 256, 1, ClampName: nameof(ArcanumSettingClamps.ListDirectoryMaxDepth)),

        // ===== Forge — Perception =====

        new("perception.maxEnumerationSteps", ConfigSection.Forge, "Perception max enumeration steps", "Maximum file/dir entries enumerated by a single Eye of the World look request.", SettingKind.Int, 1, 10_000_000, 1000, ClampName: nameof(ArcanumSettingClamps.MaxEnumerationSteps)),

        new("perception.maxTableOfContentsLines", ConfigSection.Forge, "Perception max TOC lines", "Maximum lines in the table-of-contents summary produced by a look request.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxTableOfContentsLines)),

        new("perception.allowedWorkspaceRoots", ConfigSection.Forge, "Perception allowed roots", "Absolute directory roots that GET /api/perception/look may scan. Empty (default) denies all requests with 403 Perception.PathNotAllowed.", SettingKind.StringArray),

        // ===== Forge — Spells =====

        new("spells.allowedWorkspaceRoots", ConfigSection.Forge, "Spells allowed roots", "Absolute directory roots that spell CRUD routes may use. Empty denies all access by default.", SettingKind.StringArray),

        new("spells.maxFileSizeBytes", ConfigSection.Forge, "Spell max file size (bytes)", "Maximum SPELL.md (and frontmatter) read size in bytes. Further capped by Workspaces.MaxFileReadSizeBytes.", SettingKind.Long, 1024, 1_048_576, 1024, ClampName: nameof(ArcanumSettingClamps.SpellMaxFileSizeBytes)),

        new("spells.metadataScanCacheTtlSeconds", ConfigSection.Forge, "Metadata scan cache TTL (s)", "TTL for the in-process spell-metadata scan cache used by routing and Arcane Resonance. 0 disables caching.", SettingKind.Int, 0, 300, 1, ClampName: nameof(ArcanumSettingClamps.MetadataScanCacheTtlSeconds)),

        new("spells.maxDependencies", ConfigSection.Forge, "Max dependencies", "Maximum dependencies a single spell may declare in SKILL.json.", SettingKind.Int, 0, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxDependencies)),

        new("spells.maxDeclaredTools", ConfigSection.Forge, "Max declared tools", "Maximum tools a single spell may declare in SKILL.json (Artifact Attunement allowlist).", SettingKind.Int, 0, 256, 1, ClampName: nameof(ArcanumSettingClamps.MaxDeclaredTools)),

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

        new("cli.apiRequestTimeoutSeconds", ConfigSection.Cli, "API request timeout (s)", "Timeout for non-streaming CLI API calls (lore, daemon jobs, llama status, etc.). Streaming verbs stay unbounded.", SettingKind.Int, 1, 600, 1, ClampName: nameof(ArcanumSettingClamps.ApiRequestTimeoutSeconds)),

        // ===== Cli — theme colors (Light) =====

        new("cli.themeColors.light.text", ConfigSection.Cli, "Light — text", "Body text color for the Light CLI theme.", SettingKind.Color, Placeholder: "#2A1545"),

        new("cli.themeColors.light.heading", ConfigSection.Cli, "Light — heading", "Heading text color for the Light CLI theme.", SettingKind.Color, Placeholder: "#8B1538"),

        new("cli.themeColors.light.highlight", ConfigSection.Cli, "Light — highlight", "Highlight color for the Light CLI theme.", SettingKind.Color, Placeholder: "#008F11"),

        new("cli.themeColors.light.error", ConfigSection.Cli, "Light — error", "Error message color for the Light CLI theme.", SettingKind.Color, Placeholder: "#C41E3A"),

        new("cli.themeColors.light.muted", ConfigSection.Cli, "Light — muted", "Muted/secondary text color for the Light CLI theme.", SettingKind.Color, Placeholder: "#6B5D7A"),

        // ===== Cli — theme colors (Dark) =====

        new("cli.themeColors.dark.text", ConfigSection.Cli, "Dark — text", "Body text color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#E8DCC4"),

        new("cli.themeColors.dark.heading", ConfigSection.Cli, "Dark — heading", "Heading text color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#00FFD5"),

        new("cli.themeColors.dark.highlight", ConfigSection.Cli, "Dark — highlight", "Highlight color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#39FF14"),

        new("cli.themeColors.dark.error", ConfigSection.Cli, "Dark — error", "Error message color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#FF6B6B"),

        new("cli.themeColors.dark.muted", ConfigSection.Cli, "Dark — muted", "Muted/secondary text color for the Dark CLI theme.", SettingKind.Color, Placeholder: "#7A6B90"),

    ];

    public static IReadOnlyDictionary<ConfigSection, IReadOnlyList<SettingDescriptor>> BySection { get; } =
        All.GroupBy(static d => d.Section)

            .ToDictionary(static g => g.Key, static g => (IReadOnlyList<SettingDescriptor>)g.ToList());

    public static SettingDescriptor? Find(string key) => All.FirstOrDefault(d => d.Key == key);

}
