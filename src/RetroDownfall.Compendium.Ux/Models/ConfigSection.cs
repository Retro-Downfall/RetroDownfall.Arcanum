namespace RetroDownfall.Compendium.Ux.Models;

public enum ConfigSection
{

    Host,

    Server,

    Providers,

    Intelligence,

    Mcp,

    CodingTools,

    Orchestration,

    Security,

    CommLink,

    Storage,

    Forge,

    ProvingGrounds,

    Cli,

    Resilience,

    Scrying,

    Pricing,

    Guardrails,

    Embeddings,

    Metrics,

    Files,

    Attachments,

    Batches,

    StructuredOutput,

    WebBrowsing,

    ClientToolForwarding,

    Budget,

}

public sealed record SectionDescriptor(
    ConfigSection Section,
    string Title,
    string Glyph,
    string Subtitle);

public static class SectionDescriptors
{

    public static IReadOnlyList<SectionDescriptor> All { get; } =
    [

        new(ConfigSection.Host, "General / Host", "\ue700", "Server, port, CORS, rate limit"),

        new(ConfigSection.Server, "Server", "\ue7a1", "PID file and runtime paths"),

        new(ConfigSection.Providers, "AI Providers", "\ue7c5", "OpenAI-compatible; Ollama via /v1 OK"),

        new(ConfigSection.Intelligence, "Intelligence & Context", "\ue7c8", "Inference, tokens, compression"),

        new(ConfigSection.Mcp, "MCP", "\ue7a5", "MCP client limits"),

        new(ConfigSection.CodingTools, "Coding Tools", "\ue7d8", "Search, patch, and workspace-check bounds"),

        new(ConfigSection.Orchestration, "Orchestration", "\ue7c3", "Daemon, apprentices, conclave"),

        new(ConfigSection.Security, "Security / Wards", "\ue7c6", "Wards, forbidden arts, API key"),

        new(ConfigSection.CommLink, "Comm Link", "\ue7a2", "Outbound webhook alerts"),

        new(ConfigSection.Storage, "Storage & Logging", "\ue7c1", "Grimoire, sessions, logs"),

        new(ConfigSection.Forge, "The Forge", "\ue7c4", "Spells, campaigns, prompts"),

        new(ConfigSection.ProvingGrounds, "Proving Grounds", "\ue7a6", "Trial validation bounds"),

        new(ConfigSection.Cli, "CLI", "\ue7c7", "Terminal client options and theme"),

        new(ConfigSection.Scrying, "Scrying", "\ue7ca", "Vision/multimodality image gate"),

        new(ConfigSection.Resilience, "Resilience", "\ue7c9", "Provider health probing and fallback"),

        new(ConfigSection.Pricing, "Pricing", "\ue7cc", "Per-model cost tracking"),

        new(ConfigSection.Budget, "Budget", "\ue7cd", "Daily spend limits and alerts"),

        new(ConfigSection.StructuredOutput, "Structured Output", "\ue7cf", "JSON schema validation"),

        new(ConfigSection.WebBrowsing, "Web Browsing", "\ue7d0", "browse_web tool limits"),

        new(ConfigSection.ClientToolForwarding, "Client Tools", "\ue7d1", "Forward client tools to providers"),

        new(ConfigSection.Guardrails, "Guardrails", "\ue7d2", "PII, toxicity, topic filters"),

        new(ConfigSection.Embeddings, "Embeddings", "\ue7d3", "The Weave / Divination / Saga"),

        new(ConfigSection.Metrics, "Metrics", "\ue7d4", "Prometheus /metrics endpoint"),

        new(ConfigSection.Files, "Files", "\ue7d5", "OpenAI-compatible file uploads"),

        new(ConfigSection.Attachments, "Attachments", "\ue7d7", "Session attachment persistence"),

        new(ConfigSection.Batches, "Batches", "\ue7d6", "OpenAI-compatible batch jobs"),

    ];

    public static bool IsPolished(ConfigSection section) => section switch
    {
        ConfigSection.Host
            or ConfigSection.Server
            or ConfigSection.Providers
            or ConfigSection.Intelligence
            or ConfigSection.Mcp
            or ConfigSection.Orchestration
            or ConfigSection.Security
            or ConfigSection.CommLink
            or ConfigSection.Storage
            or ConfigSection.Forge
            or ConfigSection.ProvingGrounds
            or ConfigSection.Cli
            or ConfigSection.Scrying => true,
        _ => false,
    };

    public static string? KeyPrefix(ConfigSection section) => section switch
    {
        ConfigSection.Resilience => "resilience.",
        ConfigSection.CodingTools => "codingTools.",
        ConfigSection.Pricing => "pricing.",
        ConfigSection.Budget => "budget.",
        ConfigSection.StructuredOutput => "structuredOutput.",
        ConfigSection.WebBrowsing => "webBrowsing.",
        ConfigSection.ClientToolForwarding => "clientToolForwarding.",
        ConfigSection.Guardrails => "guardrails.",
        ConfigSection.Embeddings => "embeddings.",
        ConfigSection.Metrics => "metrics.",
        ConfigSection.Files => "files.",
        ConfigSection.Attachments => "attachments.",
        ConfigSection.Batches => "batches.",
        _ => null,
    };

}
