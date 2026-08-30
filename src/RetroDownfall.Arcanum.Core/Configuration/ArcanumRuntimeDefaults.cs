namespace RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Code-owned physical limits and implementation defaults. This type is not part of the bindable
/// configuration graph.
/// </summary>
public static class ArcanumRuntimeDefaults
{

    public const int RetainedLogFileCount = 7;

    public const long HostMaxRequestBodyBytes = 10L * 1024L * 1024L;

    public const int HostAuditLogMaxSizeMb = 100;

    public const int SecurityMaxApiKeyHeaderUtf16Chars = 512;

    public const int SecurityApiKeyCacheTtlSeconds = 30;

    public const int SecurityIdempotencyTtlHours = 24;

    public const int SecurityIdempotencyMaxResponseBytes = 10 * 1024 * 1024;

    public const long WorkspaceMaxFileReadSizeBytes = 1024 * 1024;

    public const long WorkspaceMaxFileWriteSizeBytes = 1024 * 1024;

    public const long WorkspaceMaxReplaceTextBlockBytes = 512 * 1024;

    public const int DaemonShutdownDrainTimeoutSeconds = 10;

    public const int DaemonExecutionHistoryLimit = 100;

    public const long CliMaxAttachFileSizeBytes = 1_048_576L;

    public const long CliMaxAttachedTotalBytes = 32L * 1024L * 1024L;

    public const int CliMaxAttachedFileRelativePathChars = 4096;

    public const int CliDoctorHealthTimeoutSeconds = 2;

    public static IntelligenceSettings Intelligence => new();

    public static ReasoningSettings Reasoning => new()
    {
        Enabled = true,
        Summaries = false,
        DefaultEffort = ReasoningEffortLevel.Medium,
    };

    public static ServerSettings Server => new();

    public static PerceptionSettings Perception => new();

    public static SpellSettings Spells => new();

    public static CampaignsSettings Campaigns => new();

    public static GrimoireSettings Grimoire => new();

    public static SessionSettings Sessions => new();

    public static CodexSettings Codex => new();

    public static PromptSettings Prompts => new();

    public static ResilienceSettings Resilience => new() { Enabled = true };

    public static StructuredOutputSettings StructuredOutput => new();

    public static AttachmentsSettings Attachments => new();

    public static BatchesSettings Batches => new();

    public static WebBrowsingSettings WebBrowsing => new();

    public static ClientToolForwardingSettings ClientTools => new();

    public static EventBusSettings EventBus => new();

    public static LogSettings Logs => new();

    public static MetricsSettings Metrics => new();

    public static McpSettings Mcp => new();

    public static CodingToolsSettings CodingTools => new();

    public static WorkspaceCheckSettings WorkspaceChecks => new();

    public static ApprenticeSettings Apprentices => new();

    public static EmbeddingSettings Embeddings => new();

    public static ScryingSettings Scrying => new();

    public static FilesSettings Files => new();

    public static PricingSettings Pricing => new();

    public static BudgetSettings Budget => new();

    public static GuardrailsSettings Guardrails => new();

    public static WardSettings Ward => new();

    public static CommLinkSettings CommLink => new();

    public static ConclaveSettings Conclave => new();

    public static HostRateLimitSettings HostRateLimit => new();

    public static HostAuditLogSettings HostAuditLog => new();

}

/// <summary>
/// Projects the retained public schema into detached runtime settings used by implementation code
/// while the old root sections remain hard-deleted.
/// </summary>
public static class ArcanumRuntimeSettings
{

    public static IntelligenceSettings ResolveIntelligence(this ArcanumSettings settings)
    {
        IntelligenceSettings defaults = ArcanumRuntimeDefaults.Intelligence;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        ReasoningSettings reasoningDefaults = ArcanumRuntimeDefaults.Reasoning;

        return defaults with
        {
            EnableLexiconSystem = features.Lexicon,
            EnableArchiveSearch = features.ArchiveSearch,
            EnableContextCompression = true,
            EnableTokenTracking = true,
            TolerateToolFailures = true,
            UseFastModelForSpellRouting = true,
            DefaultReasoningEffort = reasoningDefaults.DefaultEffort ?? ReasoningEffortLevel.Medium,
        };
    }

    public static ConclaveSettings ResolveConclave(this ArcanumSettings settings)
    {
        ConclaveSettings defaults = ArcanumRuntimeDefaults.Conclave;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        ConclaveA2ASettings a2a = settings.ResolveA2A();

        return defaults with
        {
            Enabled = features.Conclave || a2a.Enabled,
            A2A = a2a,
        };
    }

    public static ConclaveA2ASettings ResolveA2A(this ArcanumSettings settings)
    {
        ConclaveA2ASettings defaults = ArcanumRuntimeDefaults.Conclave.A2A;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        A2AIntegrationSettings integration =
            settings.Integrations?.A2A ?? new A2AIntegrationSettings();
        bool serverEnabled = features.A2AServer;
        bool clientEnabled = features.A2AClient;

        return defaults with
        {
            Enabled = serverEnabled || clientEnabled,
            ServerEnabled = serverEnabled,
            ClientEnabled = clientEnabled,
            ServerPath = integration.ServerPath,
            AgentCardName = integration.AgentCardName,
            AgentCardDescription = integration.AgentCardDescription,
            AllowedRemoteAgents = integration.AllowedRemoteAgents ?? [],
            DefaultWorkspace = integration.DefaultWorkspace,
            OutboundCredentialEnvironmentVariable = integration.OutboundCredentialEnvironmentVariable,
            OutboundCredentialHeader = string.IsNullOrWhiteSpace(integration.OutboundCredentialHeader)
                ? defaults.OutboundCredentialHeader
                : integration.OutboundCredentialHeader.Trim(),
            Skills = integration.Skills ?? [],
            InputModes = integration.InputModes ?? [],
            OutputModes = integration.OutputModes ?? [],
            PushNotificationsEnabled = integration.PushNotifications,
            PushCallbackBaseUrl = integration.PushCallbackBaseUrl?.Trim() ?? string.Empty,
        };
    }

    public static CommLinkSettings ResolveCommLink(this ArcanumSettings settings)
    {
        CommLinkSettings defaults = ArcanumRuntimeDefaults.CommLink;
        CommLinkIntegrationSettings integration =
            settings.Integrations?.CommLink ?? new CommLinkIntegrationSettings();

        return defaults with
        {
            WebhookUrlEnvironmentVariable =
                integration.WebhookUrlEnvironmentVariable,
            AllowedSchemes = integration.AllowedSchemes ?? [],
            AllowedHosts = integration.AllowedHosts ?? [],
        };
    }

    public static McpSettings ResolveMcp(this ArcanumSettings settings)
    {
        McpSettings defaults = ArcanumRuntimeDefaults.Mcp;
        McpIntegrationSettings integration =
            settings.Integrations?.Mcp ?? new McpIntegrationSettings();

        return defaults with
        {
            AllowedHttpHosts = integration.AllowedHttpHosts ?? [],
        };
    }

    public static WorkspaceCheckSettings ResolveWorkspaceChecks(this ArcanumSettings settings)
    {
        WorkspaceCheckSettings defaults = ArcanumRuntimeDefaults.WorkspaceChecks;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        WorkspaceCheckIntegrationSettings integration =
            settings.Integrations?.WorkspaceChecks ?? new WorkspaceCheckIntegrationSettings();

        return defaults with
        {
            Enabled = features.WorkspaceChecks,
            ExecutableCatalog =
                integration.ExecutableCatalog ?? new WorkspaceCheckExecutableCatalogSettings(),
            CustomProfiles =
                integration.CustomProfiles
                ?? new Dictionary<string, WorkspaceCheckProfileSettings>(
                    StringComparer.OrdinalIgnoreCase),
        };
    }

    public static CodingToolsSettings ResolveCodingTools(this ArcanumSettings settings) =>
        ArcanumRuntimeDefaults.CodingTools with
        {
            WorkspaceCheck = settings.ResolveWorkspaceChecks(),
        };

    public static ApprenticeSettings ResolveApprentices(this ArcanumSettings settings)
    {
        ApprenticeSettings defaults = ArcanumRuntimeDefaults.Apprentices;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        ExecutionSettings execution = settings.Execution ?? new ExecutionSettings();

        return defaults with
        {
            Enabled = features.Apprentices,
            MaxConcurrentApprentices = execution.MaxConcurrentApprentices,
        };
    }

    public static EventBusSettings ResolveEventBus(this ArcanumSettings settings)
    {
        EventBusSettings defaults = ArcanumRuntimeDefaults.EventBus;
        ExecutionSettings execution = settings.Execution ?? new ExecutionSettings();

        return defaults with
        {
            MaxSseConnections = execution.MaxSseConnections,
            MaxSseConnectionsPerType = execution.MaxSseConnectionsPerType,
        };
    }

    public static LogSettings ResolveLogs(this ArcanumSettings settings)
    {
        LogSettings defaults = ArcanumRuntimeDefaults.Logs;
        HostSettings host = settings.Host ?? new HostSettings();

        return defaults with
        {
            MinLevelInBuffer = host.MinLogLevelInBuffer,
        };
    }

    public static SessionSettings ResolveSessions(this ArcanumSettings settings)
    {
        SessionSettings defaults = ArcanumRuntimeDefaults.Sessions;
        FeatureSettings features = settings.Features ?? new FeatureSettings();

        return defaults with
        {
            AllowMemoryManagement = features.MemoryManagement,
        };
    }

    public static EmbeddingSettings ResolveEmbeddings(this ArcanumSettings settings)
    {
        EmbeddingSettings defaults = ArcanumRuntimeDefaults.Embeddings;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        EmbeddingIntegrationSettings integration =
            settings.Integrations?.Embeddings ?? new EmbeddingIntegrationSettings();
        bool sagaEnabled = features.Saga || features.SagaExtraction;
        TapestryIntegrationSettings tapestry = integration.Tapestry ?? new TapestryIntegrationSettings();
        bool enabled =
            features.Embeddings
            || features.SessionSearch
            || features.CodebaseRetrieval
            || features.AttachmentRetrieval
            || sagaEnabled
            || features.Tapestry
            || features.SemanticSpellRouting;

        return defaults with
        {
            Enabled = enabled,
            Provider = integration.Provider,
            Model = integration.Model,
            Dimensions = integration.Dimensions,
            SessionSearchEnabled = features.SessionSearch,
            CodebaseRetrievalEnabled = features.CodebaseRetrieval,
            AttachmentRetrievalEnabled = features.AttachmentRetrieval,
            SagaEnabled = sagaEnabled,
            Saga = defaults.Saga with
            {
                ExtractionEnabled = features.SagaExtraction,
            },
            TapestryEnabled = features.Tapestry,
            Tapestry = defaults.Tapestry with
            {
                RetrievalMode = tapestry.RetrievalMode,
                SummaryModel = string.IsNullOrWhiteSpace(tapestry.SummaryModel)
                    ? null
                    : tapestry.SummaryModel.Trim(),
            },
            SemanticSpellRoutingEnabled = features.SemanticSpellRouting,
        };
    }

    public static ScryingSettings ResolveScrying(this ArcanumSettings settings)
    {
        ScryingSettings defaults = ArcanumRuntimeDefaults.Scrying;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        SecuritySettings security = settings.Security ?? new SecuritySettings();

        return defaults with
        {
            Enabled = features.Scrying,
            AllowedMimeTypes = security.AllowedImageMimeTypes ?? [],
        };
    }

    public static FilesSettings ResolveFiles(this ArcanumSettings settings)
    {
        FilesSettings defaults = ArcanumRuntimeDefaults.Files;
        SecuritySettings security = settings.Security ?? new SecuritySettings();

        return defaults with
        {
            AllowedMimeTypes = security.AllowedUploadMimeTypes ?? [],
        };
    }

    public static AttachmentsSettings ResolveAttachments(this ArcanumSettings settings)
    {
        AttachmentsSettings defaults = ArcanumRuntimeDefaults.Attachments;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        bool enabled = features.Attachments;

        return defaults with
        {
            Enabled = enabled,
            EnableModelAttachTool = enabled,
        };
    }

    public static BatchesSettings ResolveBatches(this ArcanumSettings settings)
    {
        BatchesSettings defaults = ArcanumRuntimeDefaults.Batches;
        ExecutionSettings execution = settings.Execution ?? new ExecutionSettings();

        return defaults with
        {
            MaxConcurrentBatches = execution.MaxConcurrentBatches,
            MaxConcurrentRequestsPerBatch = execution.MaxConcurrentRequestsPerBatch,
        };
    }

    public static BudgetSettings ResolveBudget(this ArcanumSettings settings)
    {
        BudgetSettings defaults = ArcanumRuntimeDefaults.Budget;
        BudgetPolicySettings policy =
            settings.Cost?.Budget ?? new BudgetPolicySettings();

        return defaults with
        {
            Enabled = policy.Enabled,
            DailyLimitUsd = policy.DailyLimitUsd,
        };
    }

    public static PricingSettings ResolvePricing(this ArcanumSettings settings) =>
        settings.Cost?.Pricing ?? ArcanumRuntimeDefaults.Pricing;

    public static string? ResolveDefaultWorkspace(this ArcanumSettings settings) =>
        settings.Workspaces?.DefaultRoot;

    public static string[] ResolveCampaignRoots(this ArcanumSettings settings) =>
        settings.Security?.CampaignRoots ?? [];

    public static string[] ResolveSpellRoots(this ArcanumSettings settings) =>
        settings.Security?.SpellWorkspaceRoots ?? [];

    public static string[] ResolvePerceptionRoots(this ArcanumSettings settings) =>
        settings.Security?.PerceptionWorkspaceRoots ?? [];

    public static WebBrowsingSettings ResolveWebBrowsing(this ArcanumSettings settings)
    {
        WebBrowsingSettings defaults = ArcanumRuntimeDefaults.WebBrowsing;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        WebResearchIntegrationSettings integration =
            settings.Integrations?.WebResearch ?? new WebResearchIntegrationSettings();

        return defaults with
        {
            Enabled = features.WebBrowsing,
            SearchProvider = string.IsNullOrWhiteSpace(integration.SearchProvider)
                ? defaults.SearchProvider
                : integration.SearchProvider.Trim(),
            PerplexityModel = string.IsNullOrWhiteSpace(integration.PerplexityModel)
                ? defaults.PerplexityModel
                : integration.PerplexityModel.Trim().ToLowerInvariant(),
            CredentialEnvironmentVariable = integration.CredentialEnvironmentVariable,
        };
    }

    public static ClientToolForwardingSettings ResolveClientTools(this ArcanumSettings settings)
    {
        ClientToolForwardingSettings defaults = ArcanumRuntimeDefaults.ClientTools;
        FeatureSettings features = settings.Features ?? new FeatureSettings();

        return defaults with
        {
            Enabled = features.ClientTools,
        };
    }

    public static MetricsSettings ResolveMetrics(this ArcanumSettings settings)
    {
        MetricsSettings defaults = ArcanumRuntimeDefaults.Metrics;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        SecuritySettings security = settings.Security ?? new SecuritySettings();

        return defaults with
        {
            Enabled = features.Metrics,
            RequireApiKey = security.MetricsRequireApiKey,
        };
    }

    public static WardSettings ResolveWard(this ArcanumSettings settings)
    {
        WardSettings defaults = ArcanumRuntimeDefaults.Ward;
        WardPolicySettings policy = settings.Security?.Ward ?? new WardPolicySettings();
        List<string> forbiddenArts = (defaults.ForbiddenArts ?? [])
            .Concat(policy.ForbiddenArts ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return defaults with
        {
            ForbiddenArts = forbiddenArts,
            UnattendedMode = policy.UnattendedMode,
        };
    }

    public static GuardrailsSettings ResolveGuardrails(this ArcanumSettings settings)
    {
        GuardrailsSettings defaults = ArcanumRuntimeDefaults.Guardrails;
        FeatureSettings features = settings.Features ?? new FeatureSettings();
        GuardrailsPolicySettings policy =
            settings.Security?.Guardrails ?? new GuardrailsPolicySettings();
        GuardrailsAuditPolicySettings policyAudit =
            policy.AuditLog ?? new GuardrailsAuditPolicySettings();

        return defaults with
        {
            Enabled = features.Guardrails,
            DetectPii = policy.DetectPii,
            BlockToxicity = policy.BlockToxicity,
            ToxicityBlocklist = policy.ToxicityBlocklist ?? [],
            AllowedTopics = policy.AllowedTopics ?? [],
            BlockedTopics = policy.BlockedTopics ?? [],
            StreamingMode = GuardrailsStreamingMode.Buffered,
            AuditLog = defaults.AuditLog with
            {
                Enabled = policyAudit.Enabled,
            },
        };
    }

    public static HostAuditLogSettings ResolveHostAuditLog(this ArcanumSettings settings)
    {
        HostAuditLogSettings defaults = ArcanumRuntimeDefaults.HostAuditLog;
        HostAuditPolicySettings policy =
            settings.Host?.AuditLog ?? new HostAuditPolicySettings();

        return defaults with
        {
            Enabled = policy.Enabled,
            RedactToolArguments = policy.RedactToolArguments,
        };
    }

}
