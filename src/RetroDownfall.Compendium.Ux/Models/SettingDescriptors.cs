using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

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
        new("edition", ConfigSection.Edition, "Security level", "Choose how strict security checks should be. 'Local' is safest for personal use.", SettingKind.Enum, EnumType: typeof(ArcanumEdition)),

        // Host
        new("host.port", ConfigSection.Host, "Web server port", "The port number Arcanum uses to communicate. Default is 5001.", SettingKind.Int, 1, 65_535, 1, ClampName: nameof(ArcanumSettingClamps.HostPort), Placeholder: "5001"),
        new("host.corsAllowedOrigins", ConfigSection.Host, "Allowed websites", "Websites that can access Arcanum. Add URLs like http://localhost:3000.", SettingKind.StringArray, Placeholder: "http://localhost:3000, http://localhost:8080"),
        new("host.listenAny", ConfigSection.Host, "Allow external connections", "Let other computers on your network connect to Arcanum. Requires HTTPS for security.", SettingKind.Bool),
        new("host.auditLog.enabled", ConfigSection.Host, "Keep conversation logs", "Save a record of all AI conversations for review.", SettingKind.Bool, Group: "Logging"),
        new("host.auditLog.redactToolArguments", ConfigSection.Host, "Hide sensitive details in logs", "Remove potentially sensitive information from conversation logs.", SettingKind.Bool, Group: "Logging"),
        new("host.https.enabled", ConfigSection.Host, "Enable secure connection (HTTPS)", "Use encryption for all connections. Required for external access.", SettingKind.Bool, Group: "Secure connection"),
        new("host.https.port", ConfigSection.Host, "Secure connection port", "The port number for HTTPS connections. Must be different from the regular port.", SettingKind.Int, 1, 65_535, 1, ClampName: nameof(ArcanumSettingClamps.HostHttpsPort), Group: "Secure connection", Placeholder: "5002"),
        new("host.https.certificatePath", ConfigSection.Host, "Security certificate file", "Path to your security certificate file. Leave blank to use auto-generated certificate.", SettingKind.Path, Placeholder: "~/.config/arcanum/certs/localhost.pfx", Group: "Secure connection"),
        new("host.https.privateKeyPath", ConfigSection.Host, "Private key file", "Path to your private key file (optional).", SettingKind.Path, Placeholder: "~/.config/arcanum/certs/localhost.key", Group: "Secure connection"),
        new("host.https.certificatePasswordEnvironmentVariable", ConfigSection.Host, "Certificate password variable", "Name of the environment variable containing your certificate password. Leave blank for default.", SettingKind.String, Placeholder: "ARCANUM_HTTPS_CERTIFICATE_PASSWORD", Group: "Secure connection"),
        new("host.minLogLevelInBuffer", ConfigSection.Host, "Debug information level", "How much technical detail to keep in memory for troubleshooting.", SettingKind.Enum, EnumType: typeof(RetroDownfall.Arcanum.Core.Logging.LogLevel), Group: "Debugging"),

        // Providers and top-level model selections
        new("defaultModel", ConfigSection.Providers, "Default AI model", "The AI model to use when no specific model is chosen.", SettingKind.String, Placeholder: "gpt-4o"),
        new("fastModel", ConfigSection.Providers, "Fast AI model", "A quicker AI model for simple tasks. Optional.", SettingKind.String, Placeholder: "gpt-4o-mini"),
        new("providers.name", ConfigSection.Providers, "Provider name", "A friendly name for this AI service (e.g., 'OpenAI', 'Anthropic').", SettingKind.String, Placeholder: "OpenAI"),
        new("providers.type", ConfigSection.Providers, "Provider type", "The type of AI service.", SettingKind.Enum, EnumType: typeof(AiProviderKind)),
        new("providers.endpoint", ConfigSection.Providers, "Service URL", "The web address of your AI service.", SettingKind.String, Placeholder: "https://api.openai.com/v1"),
        new("providers.credentialEnvironmentVariable", ConfigSection.Providers, "API key variable name", "Name of the environment variable containing your API key. Leave blank for automatic naming.", SettingKind.String, Placeholder: "OPENAI_API_KEY"),
        new("providers.models.name", ConfigSection.Providers, "Model name", "The exact name of the AI model as provided by the service.", SettingKind.String, Placeholder: "gpt-4-turbo-preview"),
        new("providers.models.supportsVision", ConfigSection.Providers, "Can analyze images", "Check if this model can understand and describe images.", SettingKind.Bool),
        new("providers.models.reasoning.wireDialect", ConfigSection.Providers, "Reasoning format", "Technical format for AI thinking. Only change if your provider requires a special format.", SettingKind.Enum, EnumType: typeof(ReasoningWireDialect), Group: "Advanced reasoning"),
        new("providers.models.reasoning.maxBudgetTokens", ConfigSection.Providers, "Maximum thinking tokens", "Limit how many tokens the AI can use for thinking per response. Leave blank for unlimited.", SettingKind.Int, 1, 2_097_152, 1, ClampName: nameof(ArcanumSettingClamps.ReasoningBudgetTokens), Group: "Advanced reasoning", AllowUnset: true, Placeholder: "4096"),
        new("providers.contextWindowLimit", ConfigSection.Providers, "Memory size", "How much text the AI can remember in a single conversation.", SettingKind.Int, 256, 2_097_152, 128, ClampName: nameof(ArcanumSettingClamps.ContextWindowLimit), Placeholder: "128000"),

        // Security policy
        new("security.allowUnsandboxedToolChildren", ConfigSection.Security, "Allow unrestricted file access", "Let AI tools access files outside the designated workspace. Use with caution.", SettingKind.Bool),
        new("security.metricsRequireApiKey", ConfigSection.Security, "Require API key for metrics", "Require authentication to view system metrics. Recommended for security.", SettingKind.Bool),
        new("security.ward.enabled", ConfigSection.Security, "Enable safety checks", "Require approval before running potentially dangerous commands.", SettingKind.Bool, Group: "Safety checks"),
        new("security.ward.forbiddenArts", ConfigSection.Security, "Blocked commands", "List of commands that are never allowed to run.", SettingKind.StringArray, Group: "Safety checks", Placeholder: "rm -rf, sudo"),
        new("security.ward.autoDenyInUnattendedMode", ConfigSection.Security, "Auto-reject when unattended", "Automatically deny dangerous commands when no one is watching.", SettingKind.Bool, Group: "Safety checks"),
        new("security.ward.unattendedMode", ConfigSection.Security, "Strict mode", "Apply stricter safety rules to all conversations.", SettingKind.Bool, Group: "Safety checks"),
        new("security.guardrails.detectPii", ConfigSection.Security, "Detect personal information", "Block messages containing personal data like phone numbers or addresses.", SettingKind.Bool, Group: "Content filters"),
        new("security.guardrails.blockToxicity", ConfigSection.Security, "Block harmful content", "Filter out toxic or offensive language.", SettingKind.Bool, Group: "Content filters"),
        new("security.guardrails.toxicityBlocklist", ConfigSection.Security, "Blocked words", "List of words or phrases to block (case-insensitive).", SettingKind.StringArray, Group: "Content filters", Placeholder: "word1, word2"),
        new("security.guardrails.allowedTopics", ConfigSection.Security, "Allowed topics", "Only allow conversations about these topics. Leave blank to allow all.", SettingKind.StringArray, Group: "Content filters", Placeholder: "programming, science"),
        new("security.guardrails.blockedTopics", ConfigSection.Security, "Blocked topics", "Block conversations about these topics.", SettingKind.StringArray, Group: "Content filters", Placeholder: "politics, religion"),
        new("security.guardrails.auditLog.enabled", ConfigSection.Security, "Log blocked content", "Keep a record of messages that were blocked by filters.", SettingKind.Bool, Group: "Content audit"),
        new("security.perceptionWorkspaceRoots", ConfigSection.Security, "Folders AI can read", "Folders the AI is allowed to search and read files from.", SettingKind.StringArray, Group: "Folder access", Placeholder: "/home/user/projects, /home/user/documents"),
        new("security.spellWorkspaceRoots", ConfigSection.Security, "Folders AI can modify", "Folders where the AI can create, edit, or delete files.", SettingKind.StringArray, Group: "Folder access", Placeholder: "/home/user/projects"),
        new("security.campaignRoots", ConfigSection.Security, "Allowed project folders", "Folders where projects can be created or loaded from.", SettingKind.StringArray, Group: "Folder access", Placeholder: "/home/user/projects"),
        new("security.allowedUploadMimeTypes", ConfigSection.Security, "Allowed file types for upload", "File types users can upload (e.g., application/pdf, text/plain). Leave blank for no restrictions.", SettingKind.StringArray, Group: "File restrictions", Placeholder: "application/pdf, text/plain, image/png"),
        new("security.allowedImageMimeTypes", ConfigSection.Security, "Allowed image types", "Image formats the AI can analyze (e.g., image/png, image/jpeg).", SettingKind.StringArray, Group: "File restrictions", Placeholder: "image/png, image/jpeg, image/webp"),

        // Workspaces
        new("workspaces.defaultRoot", ConfigSection.Workspaces, "Default workspace folder", "The main folder where projects and files are stored.", SettingKind.Path, Placeholder: "/home/me/projects"),
        new("workspaces.enableFileWrite", ConfigSection.Workspaces, "Allow file modifications", "Let the AI create, edit, and delete files in the workspace.", SettingKind.Bool),

        // Feature opt-ins
        new("features.enterpriseTelemetry", ConfigSection.Features, "Send usage statistics", "Share anonymous usage data to help improve the software.", SettingKind.Bool),
        new("features.scalarUi", ConfigSection.Features, "Enable API documentation", "Show interactive API documentation interface.", SettingKind.Bool),
        new("features.conclave", ConfigSection.Features, "Multi-agent collaboration", "Allow multiple AI assistants to work together on tasks.", SettingKind.Bool),
        new("features.a2AServer", ConfigSection.Features, "Accept external AI requests", "Let other AI systems send tasks to this Arcanum instance.", SettingKind.Bool),
        new("features.a2AClient", ConfigSection.Features, "Send tasks to external AI", "Allow this Arcanum to delegate tasks to other AI systems.", SettingKind.Bool),
        new("features.apprentices", ConfigSection.Features, "AI assistants", "Enable the AI assistant system that can perform tasks autonomously.", SettingKind.Bool),
        new("features.lexicon", ConfigSection.Features, "Persistent memory", "Let the AI remember information across conversations.", SettingKind.Bool),
        new("features.archiveSearch", ConfigSection.Features, "Search past conversations", "Enable searching through previous chat sessions.", SettingKind.Bool),
        new("features.metrics", ConfigSection.Features, "Performance monitoring", "Track system performance and resource usage.", SettingKind.Bool),
        new("features.embeddings", ConfigSection.Features, "Semantic search", "Enable advanced search that understands meaning, not just keywords.", SettingKind.Bool),
        new("features.sessionSearch", ConfigSection.Features, "Smart conversation search", "Find past conversations by meaning and context.", SettingKind.Bool),
        new("features.codebaseRetrieval", ConfigSection.Features, "Code search", "Search and retrieve relevant code from your projects.", SettingKind.Bool),
        new("features.attachmentRetrieval", ConfigSection.Features, "Attachment search", "Index supported text attachments and retrieve relevant excerpts within their owning conversation.", SettingKind.Bool),
        new("features.saga", ConfigSection.Features, "Long-term memory", "Enable persistent memory that grows over time.", SettingKind.Bool),
        new("features.sagaExtraction", ConfigSection.Features, "Automatic memory building", "Automatically extract and store important information from conversations.", SettingKind.Bool),
        new("features.tapestry", ConfigSection.Features, "Layered memory (The Tapestry)", "Build layered summaries of your code, attachments, and conversations so the AI can answer big-picture questions, not just find matching snippets. Uses the summary model, so it costs tokens.", SettingKind.Bool),
        new("features.semanticSpellRouting", ConfigSection.Features, "Smart command routing", "Use semantic search to find the right command for each task.", SettingKind.Bool),
        new("features.scrying", ConfigSection.Features, "Image analysis", "Allow the AI to analyze and describe images.", SettingKind.Bool),
        new("features.attachments", ConfigSection.Features, "File attachments", "Allow attaching files to conversations.", SettingKind.Bool),
        new("features.clientTools", ConfigSection.Features, "Custom tools", "Let applications define their own tools for the AI to use.", SettingKind.Bool),
        new("features.webBrowsing", ConfigSection.Features, "Web browsing", "Allow the AI to browse the internet for information.", SettingKind.Bool),
        new("features.reasoning", ConfigSection.Features, "Deep thinking", "Enable advanced reasoning capabilities for complex problems.", SettingKind.Bool, Group: "Reasoning"),
        new("features.reasoningSummaries", ConfigSection.Features, "Show thinking process", "Display the AI's reasoning steps to users.", SettingKind.Bool, Group: "Reasoning"),
        new("features.guardrails", ConfigSection.Features, "Content filters", "Apply safety filters to block inappropriate content.", SettingKind.Bool),
        new("features.workspaceChecks", ConfigSection.Features, "Workspace validation", "Verify workspace setup before running commands.", SettingKind.Bool),
        new("features.memoryManagement", ConfigSection.Features, "Memory controls", "Allow users to delete, pin, or compress conversation memory.", SettingKind.Bool),

        // Integration facts and allowlists
        new("integrations.a2A.serverPath", ConfigSection.Integrations, "AI-to-AI endpoint", "Web path for other AI systems to connect to.", SettingKind.String, Placeholder: "/api/conclave/a2a", Group: "AI collaboration"),
        new("integrations.a2A.agentCardName", ConfigSection.Integrations, "System name", "Name displayed to other AI systems.", SettingKind.String, Group: "AI collaboration", Placeholder: "My Arcanum Instance"),
        new("integrations.a2A.agentCardDescription", ConfigSection.Integrations, "System description", "Description shown to other AI systems.", SettingKind.String, Group: "AI collaboration", Placeholder: "Personal AI assistant for development tasks"),
        new("integrations.a2A.allowedRemoteAgents", ConfigSection.Integrations, "Trusted AI systems", "List of other AI systems this Arcanum may send tasks to. Leave blank to allow any public system.", SettingKind.StringArray, Group: "AI collaboration", Placeholder: "https://other-ai.example.com, https://partner.example.org"),
        new("integrations.a2A.defaultWorkspace", ConfigSection.Integrations, "Default folder for AI tasks", "Folder to use when other AI systems send tasks.", SettingKind.Path, Group: "AI collaboration", Placeholder: "/home/user/shared-workspace"),
        new("integrations.a2A.outboundCredentialEnvironmentVariable", ConfigSection.Integrations, "Key variable for other AI systems", "Environment variable holding the key this Arcanum presents when sending tasks. Leave blank to send none — required to reach another Arcanum.", SettingKind.String, Group: "AI collaboration", Placeholder: "ARCANUM_A2A_PEER_KEY"),
        new("integrations.a2A.outboundCredentialHeader", ConfigSection.Integrations, "Key header for other AI systems", "Header that carries the key. Use Authorization for systems expecting a bearer token.", SettingKind.String, Group: "AI collaboration", Placeholder: "X-Arcanum-Key"),
        new("integrations.commLink.webhookUrlEnvironmentVariable", ConfigSection.Integrations, "Webhook URL variable", "Environment variable containing the webhook URL for notifications.", SettingKind.String, Placeholder: "ARCANUM_COMMLINK_WEBHOOK_URL", Group: "Notifications"),
        new("integrations.commLink.allowedSchemes", ConfigSection.Integrations, "Allowed webhook protocols", "Protocols allowed for webhooks (e.g., https).", SettingKind.StringArray, Group: "Notifications", Placeholder: "https"),
        new("integrations.commLink.allowedHosts", ConfigSection.Integrations, "Allowed webhook hosts", "List of hosts allowed to send webhooks. Leave blank for no restrictions.", SettingKind.StringArray, Group: "Notifications", Placeholder: "api.example.com, webhook.example.org"),
        new("integrations.embeddings.provider", ConfigSection.Integrations, "Embedding service", "AI service used for semantic search.", SettingKind.String, Group: "Semantic search", Placeholder: "OpenAI"),
        new("integrations.embeddings.model", ConfigSection.Integrations, "Embedding model", "Specific model used for semantic search.", SettingKind.String, Group: "Semantic search", Placeholder: "text-embedding-3-small"),
        new("integrations.embeddings.dimensions", ConfigSection.Integrations, "Search vector size", "Size of search vectors. Changing this requires rebuilding the search index.", SettingKind.Int, 64, 4_096, 8, ClampName: nameof(ArcanumSettingClamps.EmbeddingsDimensions), Group: "Semantic search", Placeholder: "1536"),
        new("integrations.embeddings.tapestry.retrievalMode", ConfigSection.Integrations, "Layered memory search style", "How the AI reads its layered summary memory: search every level at once, or start from the broadest summary and drill down.", SettingKind.Enum, EnumType: typeof(TapestryRetrievalMode), Group: "Semantic search"),
        new("integrations.embeddings.tapestry.summaryModel", ConfigSection.Integrations, "Layered memory summary model", "AI model used to write the layered memory summaries. Leave blank to use the fast model.", SettingKind.String, Group: "Semantic search", Placeholder: "gpt-4o-mini"),
        new("integrations.mcp.allowedHttpHosts", ConfigSection.Integrations, "Allowed HTTP hosts", "Hosts allowed to use unencrypted HTTP (not recommended).", SettingKind.StringArray, Group: "Security", Placeholder: "localhost, 127.0.0.1"),
        new("integrations.webResearch.searchProvider", ConfigSection.Integrations, "Web search provider", "Native provider used by web_search.", SettingKind.String, Group: "Web research", Placeholder: "perplexity"),
        new("integrations.webResearch.perplexityModel", ConfigSection.Integrations, "Perplexity model", "Sonar model used for synthesized web search: sonar or sonar-pro.", SettingKind.String, Group: "Web research", Placeholder: "sonar"),
        new("integrations.webResearch.credentialEnvironmentVariable", ConfigSection.Integrations, "Perplexity key variable", "Optional environment variable containing the Perplexity API key; omission uses the secure local store, with ARCANUM_PERPLEXITY_API_KEY available for unattended hosts.", SettingKind.String, Group: "Web research", Placeholder: "ARCANUM_PERPLEXITY_API_KEY"),
        new("integrations.workspaceChecks.executableCatalog.dotNet.path", ConfigSection.Integrations, "Trusted .NET executable", "Path to a trusted .NET executable for running commands.", SettingKind.Path, Group: "Command execution", Placeholder: "/usr/bin/dotnet"),
        new("integrations.workspaceChecks.customProfiles", ConfigSection.Integrations, "Custom command profiles", "JSON configuration for custom build/test/lint commands.", SettingKind.Dictionary, Group: "Command execution"),

        // Host capacity and backpressure
        new("execution.maxConcurrentApprentices", ConfigSection.Execution, "Max AI assistants", "Maximum number of AI assistants running at the same time.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.MaxConcurrentApprentices), Placeholder: "5"),
        new("execution.maxConcurrentApprenticeBranches", ConfigSection.Execution, "Max parallel branches", "Maximum number of parallel tasks within a single AI assistant.", SettingKind.Int, 1, 64, 1, ClampName: nameof(ArcanumSettingClamps.MaxConcurrentApprenticeBranches), Placeholder: "3"),
        new("execution.maxConcurrentA2ATasks", ConfigSection.Execution, "Max external tasks", "Maximum number of tasks sent to other AI systems at once.", SettingKind.Int, 1, 500, 1, ClampName: nameof(ArcanumSettingClamps.MaxConcurrentA2ATasks), Placeholder: "50"),
        new("execution.maxSseConnections", ConfigSection.Execution, "Max live connections", "Maximum number of live data streams.", SettingKind.Int, 1, 100, 1, ClampName: nameof(ArcanumSettingClamps.MaxSseConnections), Placeholder: "50"),
        new("execution.maxSseConnectionsPerType", ConfigSection.Execution, "Max streams per type", "Maximum number of live streams of each type.", SettingKind.Int, 1, 50, 1, ClampName: nameof(ArcanumSettingClamps.SseConnectionsPerType), Placeholder: "20"),
        new("execution.maxConcurrentBatches", ConfigSection.Execution, "Max batch jobs", "Maximum number of batch processing jobs running at once.", SettingKind.Int, 1, 20, 1, ClampName: nameof(ArcanumSettingClamps.BatchesMaxConcurrentBatches), Placeholder: "3"),
        new("execution.maxConcurrentRequestsPerBatch", ConfigSection.Execution, "Requests per batch", "Number of requests to process in parallel within each batch.", SettingKind.Int, 1, 10, 1, ClampName: nameof(ArcanumSettingClamps.BatchesMaxConcurrentRequestsPerBatch), Placeholder: "5"),

        // Cost
        new("cost.pricing.defaultPricing.inputPer1M", ConfigSection.Cost, "Input cost per million tokens", "Default cost for text sent to the AI (USD per 1M tokens).", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingInputPer1M), Group: "Default pricing", Placeholder: "10.00"),
        new("cost.pricing.defaultPricing.outputPer1M", ConfigSection.Cost, "Output cost per million tokens", "Default cost for text received from the AI (USD per 1M tokens).", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingOutputPer1M), Group: "Default pricing", Placeholder: "30.00"),
        new("cost.pricing.defaultPricing.reasoningPer1M", ConfigSection.Cost, "Reasoning cost per million tokens", "Cost for AI thinking tokens (USD per 1M tokens). Leave blank to use output cost.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingOutputPer1M), Group: "Default pricing", AllowUnset: true, Placeholder: "60.00"),
        new("cost.pricing.defaultPricing.cachedPer1M", ConfigSection.Cost, "Cached input cost per million tokens", "Cost for cached/reused text (USD per 1M tokens).", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.PricingInputPer1M), Group: "Default pricing", Placeholder: "5.00"),
        new("cost.pricing.modelPricing", ConfigSection.Cost, "Model-specific pricing", "Custom pricing for specific AI models (JSON format).", SettingKind.Dictionary, Group: "Model pricing"),
        new("cost.budget.enabled", ConfigSection.Cost, "Enable spending limit", "Stop AI usage when daily budget is reached.", SettingKind.Bool, Group: "Budget control"),
        new("cost.budget.dailyLimitUsd", ConfigSection.Cost, "Daily spending limit (USD)", "Maximum amount to spend per day.", SettingKind.Float, 0, 1_000_000, 0.01, ClampName: nameof(ArcanumSettingClamps.BudgetDailyLimitUsd), Group: "Budget control", Placeholder: "100.00"),

        // Retention

        new("retention.automaticSweepsEnabled", ConfigSection.Retention, "Run retention sweeps automatically", "Opt in to scheduled retention sweeps. Manual preview and sweep commands remain available when this is off.", SettingKind.Bool, Group: "Sweep controls"),

        new("retention.sweepIntervalHours", ConfigSection.Retention, "Sweep interval (hours)", "How often the host should start an automatic retention sweep.", SettingKind.Int, 1, 168, 1, ClampName: nameof(ArcanumSettingClamps.RetentionSweepIntervalHours), Group: "Sweep controls", Placeholder: "24"),

        new("retention.accountingMinimumDays", ConfigSection.Retention, "Accounting minimum (days)", "Minimum period to retain accounting records, even when the accounting rule requests fewer days.", SettingKind.Int, 30, 3_650, 1, ClampName: nameof(ArcanumSettingClamps.RetentionAccountingMinimumDays), Group: "Accounting", Placeholder: "365"),

        new("retention.protectedSessionIds", ConfigSection.Retention, "Protected session IDs", "Session IDs that retention sweeps must not prune. Enter GUIDs separated by commas.", SettingKind.StringArray, Group: "Protected sessions", Placeholder: "11111111-1111-1111-1111-111111111111"),

        ..RetentionRule("activeSessions", "active sessions", "Sessions and attachments"),

        ..RetentionRule("archivedSessions", "archived sessions", "Sessions and attachments"),

        ..RetentionRule("entries", "conversation entries", "Sessions and attachments"),

        ..RetentionRule("attachments", "attachments", "Sessions and attachments"),

        ..RetentionRule("uploadedFiles", "uploaded files", "Files and batches"),

        ..RetentionRule("completedBatches", "completed batches", "Files and batches"),

        ..RetentionRule("sagaMemories", "Saga memories", "Memory and indexes"),

        ..RetentionRule("lexiconEntries", "Lexicon entries", "Memory and indexes"),

        ..RetentionRule("workspaceIndexes", "workspace indexes", "Memory and indexes"),

        ..RetentionRule("sessionEntryEmbeddings", "session-entry embeddings", "Memory and indexes"),

        ..RetentionRule("auditLogs", "audit logs", "Logs and security"),

        ..RetentionRule("guardrailLogs", "guardrail logs", "Logs and security"),

        ..RetentionRule("idempotencyClaims", "idempotency claims", "Logs and security"),

        ..RetentionRule("accounting", "accounting records", "Accounting and operations"),

        ..RetentionRule("longRunningOperations", "long-running operations", "Accounting and operations"),

        ..RetentionRule("sanctumBreaches", "Sanctum breach records", "Logs and security"),

        ..RetentionRule("daemonHistory", "daemon history records", "Accounting and operations"),

        // Daemon
        new("daemon.maxConcurrentJobs", ConfigSection.Daemon, "Max background tasks", "Maximum number of background tasks running at once.", SettingKind.Int, 1, 1_024, 1, ClampName: nameof(ArcanumSettingClamps.DaemonMaxConcurrentJobs), Placeholder: "8"),
        new("daemon.jobs.name", ConfigSection.Daemon, "Task name", "Friendly name for this background task.", SettingKind.String, Placeholder: "Daily report generation"),
        new("daemon.jobs.intervalMinutes", ConfigSection.Daemon, "Run every (minutes)", "How often to run this task (in minutes).", SettingKind.Int, 1, 10_080, 1, ClampName: nameof(ArcanumSettingClamps.UnseenServantIntervalMinutes), Placeholder: "60"),
        new("daemon.jobs.targetSpell", ConfigSection.Daemon, "Command to run", "The command or task to execute.", SettingKind.String, Placeholder: "generate_daily_report"),
        new("daemon.jobs.enabled", ConfigSection.Daemon, "Enabled", "Turn this background task on or off.", SettingKind.Bool),

        // CLI preferences
        new("cli.theme", ConfigSection.Cli, "Color theme", "Choose light or dark mode for the interface.", SettingKind.Enum, EnumType: typeof(ArcanumTheme)),
        new("cli.showManaBar", ConfigSection.Cli, "Show usage indicator", "Display a bar showing token usage during conversations.", SettingKind.Bool),
    ];

    public static IReadOnlyDictionary<ConfigSection, IReadOnlyList<SettingDescriptor>> BySection { get; } =
        All.GroupBy(static descriptor => descriptor.Section)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<SettingDescriptor>)group.ToList());

    public static SettingDescriptor? Find(string key) =>
        All.FirstOrDefault(descriptor => descriptor.Key == key);

    private static IReadOnlyList<SettingDescriptor> RetentionRule(
        string key,
        string subject,
        string group)
    {

        string titleSubject = char.ToUpperInvariant(subject[0]) + subject[1..];

        return
        [
            new(
                $"retention.{key}.enabled",
                ConfigSection.Retention,
                $"Prune {subject} automatically",
                $"Allow retention sweeps to remove {subject} after their retention period.",
                SettingKind.Bool,
                Group: group),

            new(
                $"retention.{key}.days",
                ConfigSection.Retention,
                $"{titleSubject} retention (days)",
                $"How many days to keep {subject} before they become eligible for a retention sweep.",
                SettingKind.Int,
                1,
                3_650,
                1,
                ClampName: nameof(ArcanumSettingClamps.RetentionRuleDays),
                Group: group,
                Placeholder: "30"),
        ];

    }

}
