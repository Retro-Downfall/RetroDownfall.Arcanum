using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ConfigurationValidator(ILogger<ConfigurationValidator>? logger = null)
{

    public Result Validate(ArcanumSettings settings)
    {

        List<ConfigurationValidationError> errors = [];

        ProviderSettings[] providers = settings.Providers ?? [];

        for (int providerIndex = 0; providerIndex < providers.Length; providerIndex++)
        {

            ProviderSettings provider = providers[providerIndex];

            string providerPointer = $"providers[{providerIndex}]";

            string[] models = provider.Models ?? [];

            if (provider.Type == AiProviderKind.LlamaCppServer)
            {

                bool hasModels = models.Length > 0;

                bool hasMap = provider.LlamaCpp?.ModelMap is { Count: > 0 };

                if (!hasModels && !hasMap)
                {

                    errors.Add(new ConfigurationValidationError(
                        providerPointer,
                        $"Provider '{provider.Name}' has no configured models or llamaCpp.modelMap entries."));

                }

            }
            else if (models.Length == 0)
            {

                errors.Add(new ConfigurationValidationError(
                    providerPointer,
                    $"Provider '{provider.Name}' has no configured models."));

            }

        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultModel))
        {

            if (!ModelExists(settings, settings.DefaultModel))
            {

                errors.Add(new ConfigurationValidationError(
                    "defaultModel",
                    $"DefaultModel '{settings.DefaultModel}' does not match any configured provider model."));

            }

        }

        if (!string.IsNullOrWhiteSpace(settings.FastModel))
        {

            if (!ModelExists(settings, settings.FastModel))
            {

                errors.Add(new ConfigurationValidationError(
                    "fastModel",
                    $"FastModel '{settings.FastModel}' does not match any configured provider model."));

            }

        }

        IntelligenceSettings intelligence = settings.Intelligence ?? new IntelligenceSettings();

        McpSettings mcp = settings.Mcp ?? new McpSettings();

        long toolOutputCapBytes = ArcanumSettingClamps.ToolOutputCapBytes(intelligence.ToolOutputCapBytes);

        int maxJsonRpcLineBytes = ArcanumSettingClamps.McpMaxJsonRpcLineBytes(mcp.MaxJsonRpcLineBytes);

        long effectiveToolOutputCapBytes = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            intelligence.ToolOutputCapBytes,
            maxJsonRpcLineBytes);

        if (effectiveToolOutputCapBytes < toolOutputCapBytes)
        {

            errors.Add(new ConfigurationValidationError(
                "mcp.maxJsonRpcLineBytes",
                $"Mcp.MaxJsonRpcLineBytes ({maxJsonRpcLineBytes}) is too small for Intelligence.ToolOutputCapBytes ({toolOutputCapBytes}) after JSON-RPC envelope and escaping margin."));

        }

        int executeCommandTimeoutSeconds = ArcanumSettingClamps.ExecuteCommandTimeoutSeconds(
            intelligence.ExecuteCommandTimeoutSeconds);

        int mcpRequestTimeoutSeconds = ArcanumSettingClamps.McpRequestTimeoutSeconds(
            mcp.RequestTimeoutSeconds);

        if (mcpRequestTimeoutSeconds < executeCommandTimeoutSeconds)
        {

            errors.Add(new ConfigurationValidationError(
                "mcp.requestTimeoutSeconds",
                $"Mcp.RequestTimeoutSeconds ({mcpRequestTimeoutSeconds}) must be at least Intelligence.ExecuteCommandTimeoutSeconds ({executeCommandTimeoutSeconds}) so in-process execute_command calls are not orphaned by the MCP JSON-RPC deadline."));

        }

        LlamaCppSettings llamaCpp = settings.LlamaCpp ?? new LlamaCppSettings();

        int llamaPortStart = ArcanumSettingClamps.LlamaPortStart(llamaCpp.PortStart);

        int llamaPortRange = ArcanumSettingClamps.LlamaPortRange(llamaCpp.PortRange);

        if (llamaPortStart + llamaPortRange - 1 > 65_535)
        {

            errors.Add(new ConfigurationValidationError(
                "llamaCpp.portRange",
                $"LlamaCpp.PortStart ({llamaPortStart}) + PortRange ({llamaPortRange}) - 1 exceeds 65535; the computed llama-server port can be out of range."));

        }

        ValidatePathAllowlist((settings.Campaigns ?? new CampaignsSettings()).AllowedRoots, "campaigns.allowedRoots", errors);

        ValidatePathAllowlist((settings.Spells ?? new SpellSettings()).AllowedWorkspaceRoots, "spells.allowedWorkspaceRoots", errors);

        ValidatePathAllowlist((settings.Perception ?? new PerceptionSettings()).AllowedWorkspaceRoots, "perception.allowedWorkspaceRoots", errors);

        ValidateHostWorkspace((settings.Host ?? new HostSettings()).Workspace, "host.workspace", errors);

        ValidateResilience(settings, errors);

        ValidateEventBus(settings);

        ValidateEmbeddings(settings, errors);

        if (errors.Count > 0)
        {

            return Result.Failure(new Error(
                "Configuration.ValidationFailed",
                $"{errors.Count} configuration validation error(s).",
                errors));

        }

        return Result.Success();

    }

    private void ValidateResilience(ArcanumSettings settings, List<ConfigurationValidationError> errors)
    {

        ResilienceSettings resilience = settings.Resilience ?? new ResilienceSettings();

        if (!resilience.Enabled)
        {
            return;
        }

        if (resilience.HealthProbeIntervalSeconds != ArcanumSettingClamps.HealthProbeIntervalSeconds(resilience.HealthProbeIntervalSeconds))
        {

            errors.Add(new ConfigurationValidationError(
                "resilience.healthProbeIntervalSeconds",
                $"Resilience.HealthProbeIntervalSeconds ({resilience.HealthProbeIntervalSeconds}) must be within the 5-600 clamp range."));

        }

        if (resilience.HealthRecoveryProbeIntervalSeconds != ArcanumSettingClamps.HealthRecoveryProbeIntervalSeconds(resilience.HealthRecoveryProbeIntervalSeconds))
        {

            errors.Add(new ConfigurationValidationError(
                "resilience.healthRecoveryProbeIntervalSeconds",
                $"Resilience.HealthRecoveryProbeIntervalSeconds ({resilience.HealthRecoveryProbeIntervalSeconds}) must be within the 5-3,600 clamp range."));

        }

        if (resilience.HealthFailureThreshold != ArcanumSettingClamps.HealthFailureThreshold(resilience.HealthFailureThreshold))
        {

            errors.Add(new ConfigurationValidationError(
                "resilience.healthFailureThreshold",
                $"Resilience.HealthFailureThreshold ({resilience.HealthFailureThreshold}) must be within the 1-100 clamp range."));

        }

        if (resilience.MaxFallbackAttempts != ArcanumSettingClamps.MaxFallbackAttempts(resilience.MaxFallbackAttempts))
        {

            errors.Add(new ConfigurationValidationError(
                "resilience.maxFallbackAttempts",
                $"Resilience.MaxFallbackAttempts ({resilience.MaxFallbackAttempts}) must be within the 1-10 clamp range."));

        }

        if (resilience.HealthProbeTimeoutSeconds != ArcanumSettingClamps.HealthProbeTimeoutSeconds(resilience.HealthProbeTimeoutSeconds))
        {

            errors.Add(new ConfigurationValidationError(
                "resilience.healthProbeTimeoutSeconds",
                $"Resilience.HealthProbeTimeoutSeconds ({resilience.HealthProbeTimeoutSeconds}) must be within the 1-30 clamp range."));

        }

        if ((settings.Providers ?? []).Length == 0)
        {

            // Not a failure — providers can be added later via hot-reload. Operators should still see
            // a signal that resilience is enabled with nothing to probe yet.
            logger?.LogWarning(
                "Arcanum:Resilience:Enabled is true but Arcanum:Providers is empty. The probe scheduler will idle until providers are configured.");

        }

    }

    private void ValidateEventBus(ArcanumSettings settings)
    {

        EventBusSettings eventBus = settings.EventBus ?? new EventBusSettings();

        int maxConnections = ArcanumSettingClamps.MaxSseConnections(eventBus.MaxSseConnections);

        int perTypeLimit = ArcanumSettingClamps.SseConnectionsPerType(eventBus.MaxSseConnectionsPerType);

        if (maxConnections > 0 && perTypeLimit > maxConnections)
        {

            // Not a failure — the global cap triggers first and remains safe, but the per-type
            // cap can never engage, which likely does not match operator intent.
            logger?.LogWarning(
                "Arcanum:EventBus:MaxSseConnectionsPerType ({PerTypeLimit}) exceeds Arcanum:EventBus:MaxSseConnections ({MaxConnections}); the global cap will always trigger first, making the per-type cap meaningless.",
                perTypeLimit,
                maxConnections);

        }

    }

    /// <summary>
    /// RAG Phase 1 — The Weave &amp; Divination. When <c>Arcanum:Embeddings:Enabled</c> is <c>true</c>,
    /// <c>Provider</c> must reference an existing provider name and <c>Model</c> must be non-empty.
    /// Every per-feature flag (Phase 2 <c>SessionSearchEnabled</c>, Phase 3
    /// <c>CodebaseRetrievalEnabled</c>, Phase 4 <c>SagaEnabled</c>, Phase 5
    /// <c>SemanticSpellRoutingEnabled</c>) requires <c>Enabled</c> to also be <c>true</c> — a flag
    /// cannot be on while the shared embedding foundation is off.
    /// </summary>
    private static void ValidateEmbeddings(ArcanumSettings settings, List<ConfigurationValidationError> errors)
    {

        EmbeddingSettings embeddings = settings.Embeddings ?? new EmbeddingSettings();

        if (embeddings.Enabled)
        {

            if (string.IsNullOrWhiteSpace(embeddings.Provider))
            {

                errors.Add(new ConfigurationValidationError(
                    "embeddings.provider",
                    "Arcanum:Embeddings:Provider is required when Arcanum:Embeddings:Enabled is true."));

            }
            else if (!ProviderResolver.TryResolveProviderByName(settings, embeddings.Provider, out _))
            {

                errors.Add(new ConfigurationValidationError(
                    "embeddings.provider",
                    $"Arcanum:Embeddings:Provider '{embeddings.Provider}' does not match any configured provider."));

            }

            if (string.IsNullOrWhiteSpace(embeddings.Model))
            {

                errors.Add(new ConfigurationValidationError(
                    "embeddings.model",
                    "Arcanum:Embeddings:Model is required when Arcanum:Embeddings:Enabled is true."));

            }

        }

        if (embeddings.SessionSearchEnabled && !embeddings.Enabled)
        {

            errors.Add(new ConfigurationValidationError(
                "embeddings.sessionSearchEnabled",
                "Arcanum:Embeddings:SessionSearchEnabled requires Arcanum:Embeddings:Enabled to be true."));

        }

        if (embeddings.CodebaseRetrievalEnabled && !embeddings.Enabled)
        {

            errors.Add(new ConfigurationValidationError(
                "embeddings.codebaseRetrievalEnabled",
                "Arcanum:Embeddings:CodebaseRetrievalEnabled requires Arcanum:Embeddings:Enabled to be true."));

        }

        if (embeddings.SagaEnabled && !embeddings.Enabled)
        {

            errors.Add(new ConfigurationValidationError(
                "embeddings.sagaEnabled",
                "Arcanum:Embeddings:SagaEnabled requires Arcanum:Embeddings:Enabled to be true."));

        }

        if (embeddings.SemanticSpellRoutingEnabled && !embeddings.Enabled)
        {

            errors.Add(new ConfigurationValidationError(
                "embeddings.semanticSpellRoutingEnabled",
                "Arcanum:Embeddings:SemanticSpellRoutingEnabled requires Arcanum:Embeddings:Enabled to be true."));

        }

    }

    private static bool ModelExists(ArcanumSettings settings, string model)
    {

        ProviderSettings[] providers = settings.Providers ?? [];

        for (int i = 0; i < providers.Length; i++)
        {

            foreach (string configured in ProviderResolver.EnumerateAdvertisedModels(providers[i]))
            {

                if (ProviderResolver.ModelNameMatches(configured, model))
                {

                    return true;

                }

            }

        }

        return false;

    }

    private static void ValidatePathAllowlist(
        string[]? roots,
        string pointer,
        List<ConfigurationValidationError> errors)
    {

        foreach (string root in roots ?? [])
        {

            if (string.IsNullOrWhiteSpace(root))
            {

                errors.Add(new ConfigurationValidationError(pointer, $"Path allowlist entry must not be empty."));

                continue;

            }

            if (!Path.IsPathRooted(root))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Path allowlist entry '{root}' must be an absolute path."));

            }
            else if (!Directory.Exists(root))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Path allowlist entry '{root}' does not exist or is not a directory."));

            }

        }

    }

    private static void ValidateHostWorkspace(
        string? workspace,
        string pointer,
        List<ConfigurationValidationError> errors)
    {

        if (string.IsNullOrWhiteSpace(workspace))
        {

            return;
        }

        string rooted = Path.IsPathRooted(workspace) ? workspace : Path.GetFullPath(workspace);

        if (!Directory.Exists(rooted))
        {

            errors.Add(new ConfigurationValidationError(
                pointer,
                $"Host workspace '{workspace}' does not exist or is not a directory."));

        }

    }

}
