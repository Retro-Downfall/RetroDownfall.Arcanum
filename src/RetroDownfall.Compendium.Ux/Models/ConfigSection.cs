namespace RetroDownfall.Compendium.Ux.Models;

public enum ConfigSection
{

    Host,

    Server,

    Providers,

    Intelligence,

    Mcp,

    LlamaCpp,

    Orchestration,

    Security,

    CommLink,

    Storage,

    Forge,

    ProvingGrounds,

    Cli,

    Resilience,

    Scrying,

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

        new(ConfigSection.Providers, "AI Providers", "\ue7c5", "OpenAI-compatible (incl. Ollama), LlamaCpp"),

        new(ConfigSection.Intelligence, "Intelligence & Context", "\ue7c8", "Inference, tokens, compression"),

        new(ConfigSection.Mcp, "MCP", "\ue7a5", "MCP client limits"),

        new(ConfigSection.LlamaCpp, "LlamaCpp", "\ue7c2", "Local GGUF server settings"),

        new(ConfigSection.Orchestration, "Orchestration", "\ue7c3", "Daemon, apprentices, conclave"),

        new(ConfigSection.Security, "Security / Wards", "\ue7c6", "Wards, forbidden arts, API key"),

        new(ConfigSection.CommLink, "Comm Link", "\ue7a2", "Outbound webhook alerts"),

        new(ConfigSection.Storage, "Storage & Logging", "\ue7c1", "Grimoire, sessions, logs"),

        new(ConfigSection.Forge, "The Forge", "\ue7c4", "Spells, campaigns, prompts"),

        new(ConfigSection.ProvingGrounds, "Proving Grounds", "\ue7a6", "Trial validation bounds"),

        new(ConfigSection.Cli, "CLI", "\ue7c7", "Terminal client options and theme"),

        new(ConfigSection.Resilience, "Resilience", "\ue7c9", "Provider health probing and fallback"),

        new(ConfigSection.Scrying, "Scrying", "\ue7ca", "Vision/multimodality image gate"),

    ];

}
