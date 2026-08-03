namespace RetroDownfall.Compendium.Ux.Models;

public enum ConfigSection
{

    Presets,

    Edition,

    Host,

    Providers,

    Security,

    Workspaces,

    Features,

    Integrations,

    Execution,

    Cost,

    Retention,

    Daemon,

    Cli,

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
        new(ConfigSection.Presets, "Presets", "\ue8f1", "Workflow profiles, previews, and effective state"),
        new(ConfigSection.Edition, "Edition", "\ue713", "Runtime hardening mode"),
        new(ConfigSection.Host, "Host", "\ue700", "Binding, HTTPS, CORS, audit, and logs"),
        new(ConfigSection.Providers, "Providers & Models", "\ue7c5", "Endpoints, models, and factual capabilities"),
        new(ConfigSection.Security, "Security", "\ue7c6", "Wards, guardrails, permissions, and allowlists"),
        new(ConfigSection.Workspaces, "Workspaces", "\ue8b7", "Default root and write permission"),
        new(ConfigSection.Features, "Features", "\ue7c8", "Explicit capability opt-ins"),
        new(ConfigSection.Integrations, "Integrations", "\ue774", "A2A, CommLink, embeddings, MCP, and checks"),
        new(ConfigSection.Execution, "Execution", "\ue7c3", "Host concurrency and backpressure"),
        new(ConfigSection.Cost, "Cost", "\ue7cc", "Provider pricing and daily budget"),

        new(ConfigSection.Retention, "Retention", "\ue74d", "Data lifecycle, pruning, and protected sessions"),

        new(ConfigSection.Daemon, "Daemon", "\ue7a6", "Unseen Servant schedules and concurrency"),
        new(ConfigSection.Cli, "CLI", "\ue7c7", "Terminal preferences"),
    ];

    public static bool IsPolished(ConfigSection section) => section switch
    {
        ConfigSection.Host
            or ConfigSection.Providers
            or ConfigSection.Daemon
            or ConfigSection.Cli
            or ConfigSection.Presets => true,
        _ => false,
    };

}
