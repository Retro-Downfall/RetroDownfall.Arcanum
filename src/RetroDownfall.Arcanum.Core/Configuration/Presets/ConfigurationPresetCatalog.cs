using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Configuration.Presets;

/// <summary>
/// Immutable, code-authored preset definitions shared by every frontend. A preset is a partial
/// overlay: values not listed in <see cref="ConfigurationPresetDefinition.OwnedSettings"/> remain
/// operator-owned.
/// </summary>
public static class ConfigurationPresetCatalog
{

    public const string ProviderModelPrerequisite = "provider-model";

    public const string WorkspacePrerequisite = "workspace";

    public const string ResearchCredentialPrerequisite = "research-credential";

    public const string LoopbackProviderPrerequisite = "loopback-provider";

    public const string PositiveBudgetPrerequisite = "positive-budget";

    public static ImmutableArray<ConfigurationPresetDefinition> Versions { get; } =
    [
        GeneralAssistant(),
        CodingWorkspace(),
        Research(),
        PrivateOffline(),
        Automation(),
        AdvancedCustom(),
    ];

    public static ImmutableArray<ConfigurationPresetDefinition> All { get; } = Versions
        .GroupBy(static preset => preset.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static versions => versions.MaxBy(static preset => preset.Version)!)
        .ToImmutableArray();

    public static ImmutableArray<ConfigurationPresetGlossaryEntry> Glossary { get; } =
    [
        new(
            "Ward",
            "An approval gate for tool actions."),
        new(
            "Sanctum",
            "A workspace sandbox that enforces approved path boundaries."),
        new(
            "Weave",
            "A semantic index for approved workspace content."),
        new(
            "Saga",
            "Long-term memory extracted from completed work."),
        new(
            "Lexicon",
            "Explicit entity memory added to context when relevant."),
    ];

    public static ConfigurationPresetDefinition? Find(string? idOrName)
    {

        if (string.IsNullOrWhiteSpace(idOrName))
        {

            return null;

        }

        string query = idOrName.Trim();

        return All.FirstOrDefault(preset =>
            string.Equals(preset.Id, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(preset.DisplayName, query, StringComparison.OrdinalIgnoreCase));

    }

    public static ConfigurationPresetDefinition? FindVersion(string? id, int version)
    {

        if (string.IsNullOrWhiteSpace(id) || version <= 0)
        {

            return null;

        }

        return Versions.FirstOrDefault(preset =>
            string.Equals(preset.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)
            && preset.Version == version);

    }

    private static ConfigurationPresetDefinition GeneralAssistant() =>
        new(
            "general-assistant",
            1,
            "General Assistant",
            "A balanced conversational setup with attachments and conservative memory defaults.",
            Owned(
                Setting("features.attachments", "true"),
                SafetySetting("features.saga", "false"),
                SafetySetting("features.sagaExtraction", "false"),
                SafetySetting("features.memoryManagement", "false"),
                SafetySetting("security.ward.enabled", "true"),
                SafetySetting("security.ward.autoDenyInUnattendedMode", "true"),
                SafetySetting("security.allowUnsandboxedToolChildren", "false")),
            new ConfigurationPresetDisclosure(
                "Attachments and normal guarded tool use.",
                "Automatic long-term-memory extraction and destructive memory management.",
                "Ward remains enabled and unsandboxed child processes remain disabled.",
                "A configured inference provider and model are required for first success.",
                "No budget or concurrency limit is changed."),
            Prerequisites(ProviderModel()),
            Recommendations(new ConfigurationPresetRecommendation(
                "Verify the selected model can answer a simple request.",
                "arcanum run \"Hello\"")),
            Progressive(
                "Choose the provider and model you intend to use.",
                "Saga, semantic retrieval, MCP servers, and automation remain optional.",
                "Run arcanum run \"Hello\"."));

    private static ConfigurationPresetDefinition CodingWorkspace() =>
        new(
            "coding-workspace",
            1,
            "Coding Workspace",
            "Enables workspace checks and file editing under the configured default workspace root.",
            Owned(
                Setting("features.workspaceChecks", "true"),
                Setting("workspaces.enableFileWrite", "true"),
                SafetySetting("security.ward.enabled", "true"),
                SafetySetting("security.ward.autoDenyInUnattendedMode", "true"),
                SafetySetting("security.allowUnsandboxedToolChildren", "false")),
            new ConfigurationPresetDisclosure(
                "Workspace validation and workspace-scoped file writes.",
                "Nothing outside the owned values; indexing remains an explicit later choice.",
                "File changes can modify project data; Ward and Sanctum boundaries remain active.",
                "A configured inference provider/model and a workspace are required.",
                "No research, apprentice, retry, timeout, or indexing limit is changed."),
            Prerequisites(ProviderModel(), Workspace()),
            Recommendations(
                new ConfigurationPresetRecommendation(
                    "Run a first workspace-scoped coding request.",
                    "arcanum run --workspace . \"Inspect this workspace and summarize it.\""),
                new ConfigurationPresetRecommendation(
                    "Add semantic code retrieval only when the workspace benefits from it.",
                    "arcanum workspace index .",
                    IsAdvancedFeature: true)),
            Progressive(
                "Configure the default workspace root that Arcanum may edit.",
                "Codebase indexing, apprentices, and custom workspace checks remain optional.",
                "Run arcanum run --workspace . \"Inspect this workspace and summarize it.\""));

    private static ConfigurationPresetDefinition Research() =>
        new(
            "research",
            1,
            "Research",
            "Enables native web research while preserving explicit provider and credential setup.",
            Owned(
                Setting(
                    "features.webBrowsing",
                    "true",
                    ResearchCredentialPrerequisite),
                SafetySetting("security.ward.enabled", "true"),
                SafetySetting("security.ward.autoDenyInUnattendedMode", "true"),
                SafetySetting("security.allowUnsandboxedToolChildren", "false")),
            new ConfigurationPresetDisclosure(
                "Native web search and URL reading.",
                "No local memory, citation, retry, hop, timeout, or loop setting is changed.",
                "Research sends queries and selected page requests to external services.",
                "An inference provider/model and a securely stored Perplexity credential are required.",
                "External research can incur provider cost; existing explicit budgets remain unchanged."),
            Prerequisites(ProviderModel(), ResearchCredential()),
            Recommendations(new ConfigurationPresetRecommendation(
                "Run one cited research request.",
                "arcanum run --research \"What changed?\"")),
            Progressive(
                "Store the research credential and review the external-data disclosure.",
                "Semantic memory and custom research workflows remain optional.",
                "Run arcanum run --research \"What changed?\"."));

    private static ConfigurationPresetDefinition PrivateOffline() =>
        new(
            "private-offline",
            1,
            "Private/Offline",
            "Keeps the primary runtime loopback-only and turns off built-in external research and telemetry.",
            Owned(
                SafetySetting("host.listenAny", "false"),
                SafetySetting("features.webBrowsing", "false"),
                SafetySetting("features.enterpriseTelemetry", "false"),
                SafetySetting("security.ward.enabled", "true"),
                SafetySetting("security.ward.autoDenyInUnattendedMode", "true"),
                SafetySetting("security.allowUnsandboxedToolChildren", "false")),
            new ConfigurationPresetDisclosure(
                "Loopback inference with local attachments and guarded tools.",
                "Built-in external web research, enterprise telemetry, and non-loopback host binding.",
                "Configured third-party integrations are not erased; inspect them before assuming fully offline operation.",
                "The selected inference provider endpoint must be loopback.",
                "No budget, storage-retention, or concurrency value is changed."),
            Prerequisites(LoopbackProvider()),
            Recommendations(new ConfigurationPresetRecommendation(
                "Verify the local provider answers without external research.",
                "arcanum run \"Hello\"")),
            Progressive(
                "Choose a loopback provider and model.",
                "Review MCP and other authored integration allowlists separately if strict offline operation is required.",
                "Run arcanum run \"Hello\"."));

    private static ConfigurationPresetDefinition Automation() =>
        new(
            "automation",
            1,
            "Automation",
            "Enables unattended Ward behavior only after an operator supplies a positive explicit budget.",
            Owned(
                SafetySetting("security.ward.enabled", "true"),
                SafetySetting("security.ward.autoDenyInUnattendedMode", "true"),
                Setting("security.ward.unattendedMode", "true"),
                SafetySetting("security.allowUnsandboxedToolChildren", "false")),
            new ConfigurationPresetDisclosure(
                "Unattended execution under Ward auto-denial.",
                "No forbidden-art bypass, unsandboxed child process, destructive memory action, or untrusted MCP server.",
                "Actions that require approval are denied while unattended; existing tool permissions still apply.",
                "A configured inference provider/model is required.",
                "An already enabled, positive daily budget is required and is never invented or enlarged."),
            Prerequisites(ProviderModel(), PositiveBudget()),
            Recommendations(new ConfigurationPresetRecommendation(
                "Inspect daemon state before scheduling unattended work.",
                "arcanum daemon status")),
            Progressive(
                "Set and review an explicit positive daily budget.",
                "Jobs, schedules, apprentices, and integration-specific permissions remain separate choices.",
                "Run arcanum daemon status."));

    private static ConfigurationPresetDefinition AdvancedCustom() =>
        new(
            "advanced-custom",
            1,
            "Advanced/Custom",
            "Leaves every configuration value operator-owned while exposing the same inspection tools.",
            [],
            new ConfigurationPresetDisclosure(
                "No capability automatically.",
                "Nothing automatically.",
                "The operator is responsible for reviewing every enabled capability and boundary.",
                "No provider is selected or modified.",
                "No budget, concurrency, retry, timeout, or loop value is changed."),
            [],
            Recommendations(new ConfigurationPresetRecommendation(
                "Inspect and validate the effective configuration.",
                "arcanum config show")),
            Progressive(
                "Review the effective configuration before enabling advanced features.",
                "All features remain individually configurable.",
                "Run arcanum config show."));

    private static ConfigurationPresetPrerequisite ProviderModel() =>
        new(
            ProviderModelPrerequisite,
            "A configured provider must advertise the selected model.",
            "arcanum config open",
            Required: true);

    private static ConfigurationPresetPrerequisite Workspace() =>
        new(
            WorkspacePrerequisite,
            "A default workspace root must be configured.",
            "arcanum config set workspaces.defaultRoot .",
            Required: true);

    private static ConfigurationPresetPrerequisite ResearchCredential() =>
        new(
            ResearchCredentialPrerequisite,
            "The native research provider credential must already be stored securely.",
            "arcanum key provider set perplexity",
            Required: true);

    private static ConfigurationPresetPrerequisite LoopbackProvider() =>
        new(
            LoopbackProviderPrerequisite,
            "The selected provider endpoint must resolve to this computer's loopback interface.",
            "arcanum config open",
            Required: true);

    private static ConfigurationPresetPrerequisite PositiveBudget() =>
        new(
            PositiveBudgetPrerequisite,
            "Daily budget enforcement must already be enabled with a value greater than zero.",
            "arcanum config open",
            Required: true);

    private static ConfigurationPresetOwnedSetting Setting(
        string path,
        string canonicalJson,
        params string[] prerequisiteIds) =>
        new(
            path,
            canonicalJson,
            RequiresRestart: true,
            [.. prerequisiteIds]);

    private static ConfigurationPresetOwnedSetting SafetySetting(
        string path,
        string canonicalJson,
        params string[] prerequisiteIds) =>
        new(
            path,
            canonicalJson,
            RequiresRestart: true,
            [.. prerequisiteIds],
            IsSafetyBoundary: true);

    private static ImmutableArray<ConfigurationPresetOwnedSetting> Owned(
        params ConfigurationPresetOwnedSetting[] settings) =>
        [.. settings];

    private static ImmutableArray<ConfigurationPresetPrerequisite> Prerequisites(
        params ConfigurationPresetPrerequisite[] prerequisites) =>
        [.. prerequisites];

    private static ImmutableArray<ConfigurationPresetRecommendation> Recommendations(
        params ConfigurationPresetRecommendation[] recommendations) =>
        [.. recommendations];

    private static ConfigurationPresetProgressiveDisclosure Progressive(
        string essentialChoice,
        string deferredFeatures,
        string firstSuccessRecommendation) =>
        new(
            essentialChoice,
            [deferredFeatures],
            firstSuccessRecommendation);

}
