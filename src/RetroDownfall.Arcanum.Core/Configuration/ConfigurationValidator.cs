using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ConfigurationValidator(ILogger<ConfigurationValidator>? logger = null)
{

    internal const string ObsoleteLlamaCppMigrationMessage =
        "Arcanum:LlamaCpp is no longer supported. Configure an OpenAI-compatible HTTP provider (type OpenAICompatible) instead; Ollama may use endpoint http://localhost:11434/v1.";

    internal const string ObsoleteLlamaCppServerTypeMessage =
        "Provider type LlamaCppServer is no longer supported. Configure an OpenAI-compatible HTTP provider (type OpenAICompatible) instead; Ollama may use endpoint http://localhost:11434/v1.";

    internal const string ObsoleteCacheMigrationMessage =
        "Arcanum:Cache is no longer supported. Prompt caching is provider-managed; use ProviderSettings.SupportsPromptCaching to gate cached-token metrics.";

    internal const string ObsoleteProviderLlamaCppMessage =
        "Provider-level llamaCpp (including modelMap) is no longer supported. List models explicitly under Providers[].Models.";

    internal const string ObsoleteProviderModelMapMessage =
        "Provider-level modelMap is no longer supported. List models explicitly under Providers[].Models.";

    /// <summary>
    /// Rejects obsolete configuration keys that binding would otherwise silently ignore after the
    /// corresponding options properties were removed (managed local-inference options / global Cache).
    /// Also rejects obsolete provider <c>Type</c> values such as <c>LlamaCppServer</c> before options
    /// binding can fail with a generic enum-conversion error.
    /// </summary>
    public Result RejectObsoleteKeys(IConfiguration configuration)
    {

        List<ConfigurationValidationError> errors = [];

        IConfigurationSection arcanum = configuration.GetSection("Arcanum");

        if (arcanum.GetSection("LlamaCpp").Exists())
        {
            errors.Add(new ConfigurationValidationError(
                "llamaCpp",
                ObsoleteLlamaCppMigrationMessage));
        }

        if (arcanum.GetSection("Cache").Exists())
        {
            errors.Add(new ConfigurationValidationError(
                "cache",
                ObsoleteCacheMigrationMessage));
        }

        IConfigurationSection providers = arcanum.GetSection("Providers");

        foreach (IConfigurationSection provider in providers.GetChildren())
        {
            string pointer = string.IsNullOrEmpty(provider.Key)
                ? "providers"
                : $"providers[{provider.Key}]";

            if (provider.GetSection("LlamaCpp").Exists() || provider.GetSection("llamaCpp").Exists())
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.llamaCpp",
                    ObsoleteProviderLlamaCppMessage));
            }

            if (provider.GetSection("ModelMap").Exists() || provider.GetSection("modelMap").Exists())
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.modelMap",
                    ObsoleteProviderModelMapMessage));
            }

            string? typeValue = provider["Type"] ?? provider["type"];

            if (IsObsoleteLlamaCppServerType(typeValue))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.type",
                    ObsoleteLlamaCppServerTypeMessage));
            }
        }

        if (errors.Count > 0)
        {
            return Result.Failure(new Error(
                "Configuration.ValidationFailed",
                $"{errors.Count} obsolete configuration key(s).",
                errors));
        }

        return Result.Success();

    }

    /// <summary>
    /// Rejects obsolete keys and obsolete provider types in an ArcanumSettings-shaped JSON object
    /// (API PUT/validate bodies). Must run on raw <see cref="JsonDocument"/> / <see cref="JsonElement"/>
    /// before source-generated deserialization so <c>type: LlamaCppServer</c> yields a migration
    /// error instead of a generic invalid-body enum failure.
    /// </summary>
    public Result RejectObsoleteJsonKeys(JsonElement root)
    {

        List<ConfigurationValidationError> errors = [];

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Result.Success();
        }

        if (TryGetPropertyIgnoreCase(root, "llamaCpp", out _))
        {
            errors.Add(new ConfigurationValidationError(
                "llamaCpp",
                ObsoleteLlamaCppMigrationMessage));
        }

        if (TryGetPropertyIgnoreCase(root, "cache", out _))
        {
            errors.Add(new ConfigurationValidationError(
                "cache",
                ObsoleteCacheMigrationMessage));
        }

        if (TryGetPropertyIgnoreCase(root, "providers", out JsonElement providers)
            && providers.ValueKind == JsonValueKind.Array)
        {
            int index = 0;

            foreach (JsonElement provider in providers.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object)
                {
                    index++;

                    continue;
                }

                if (TryGetPropertyIgnoreCase(provider, "llamaCpp", out _))
                {
                    errors.Add(new ConfigurationValidationError(
                        $"providers[{index}].llamaCpp",
                        ObsoleteProviderLlamaCppMessage));
                }

                if (TryGetPropertyIgnoreCase(provider, "modelMap", out _))
                {
                    errors.Add(new ConfigurationValidationError(
                        $"providers[{index}].modelMap",
                        ObsoleteProviderModelMapMessage));
                }

                if (TryGetPropertyIgnoreCase(provider, "type", out JsonElement typeElement)
                    && typeElement.ValueKind == JsonValueKind.String
                    && IsObsoleteLlamaCppServerType(typeElement.GetString()))
                {
                    errors.Add(new ConfigurationValidationError(
                        $"providers[{index}].type",
                        ObsoleteLlamaCppServerTypeMessage));
                }

                index++;
            }
        }

        if (errors.Count > 0)
        {
            return Result.Failure(new Error(
                "Configuration.ValidationFailed",
                $"{errors.Count} obsolete configuration key(s).",
                errors));
        }

        return Result.Success();

    }

    private static bool IsObsoleteLlamaCppServerType(string? typeValue) =>
        !string.IsNullOrWhiteSpace(typeValue)
        && string.Equals(typeValue.Trim(), "LlamaCppServer", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;

                return true;
            }
        }

        value = default;

        return false;

    }

    public Result Validate(ArcanumSettings settings)
    {

        List<ConfigurationValidationError> errors = [];

        ProviderSettings[] providers = settings.Providers ?? [];

        HashSet<string> seenProviderNames = new(StringComparer.OrdinalIgnoreCase);

        for (int providerIndex = 0; providerIndex < providers.Length; providerIndex++)
        {

            ProviderSettings provider = providers[providerIndex];

            string providerPointer = $"providers[{providerIndex}]";

            // Missing Type defaults to OpenAICompatible (enum zero). Reject undefined numeric leftovers
            // and any defined non-OpenAICompatible value; do not require Type to be present.
            if (!Enum.IsDefined(provider.Type) || provider.Type != AiProviderKind.OpenAICompatible)
            {

                errors.Add(new ConfigurationValidationError(
                    $"{providerPointer}.type",
                    $"Provider '{provider.Name}' type must be OpenAICompatible (Ollama via http://localhost:11434/v1)."));

            }

            IReadOnlyList<ModelEntry> models = provider.Models ?? [];

            if (models.Count == 0)
            {

                errors.Add(new ConfigurationValidationError(
                    providerPointer,
                    $"Provider '{provider.Name}' has no configured models."));

            }

            // OpenAICompatible providers dial provider.Endpoint directly at inference time
            // (ChatClientFactory/EmbeddingGeneratorFactory both do `new Uri(provider.Endpoint)`
            // unguarded). An empty Endpoint is left as-is here (a provider mid-setup, not yet
            // pointed anywhere, is a pre-existing accepted state throughout this validator's own
            // test suite) — but when a value IS configured, it must be a well-formed absolute
            // http/https URI, catching a typo as a clear startup error instead of a runtime
            // UriFormatException on the first request, or the health probe silently reporting the
            // provider unhealthy with no indication why.
            if (provider.Type == AiProviderKind.OpenAICompatible
                && !string.IsNullOrWhiteSpace(provider.Endpoint)
                && (!Uri.TryCreate(provider.Endpoint.Trim(), UriKind.Absolute, out Uri? endpointUri)
                    || (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps)))
            {

                errors.Add(new ConfigurationValidationError(
                    providerPointer,
                    $"Provider '{provider.Name}' Endpoint '{provider.Endpoint}' must be an absolute http or https URI."));

            }

            if (!string.IsNullOrWhiteSpace(provider.Name) && !seenProviderNames.Add(provider.Name))
            {

                // Provider health is keyed by Name (ProviderHealthTracker), and ProviderResolver's
                // by-name lookups (TryResolveProviderByName, used by embeddings config) return only
                // the first match — a duplicate name would otherwise silently share health state and
                // resolve to the wrong provider for the second entry.
                errors.Add(new ConfigurationValidationError(
                    providerPointer,
                    $"Provider name '{provider.Name}' is configured more than once; provider names must be unique."));

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

        ValidatePathAllowlist((settings.Campaigns ?? new CampaignsSettings()).AllowedRoots, "campaigns.allowedRoots", errors);


        ValidatePathAllowlist((settings.Spells ?? new SpellSettings()).AllowedWorkspaceRoots, "spells.allowedWorkspaceRoots", errors);

        ValidatePathAllowlist((settings.Perception ?? new PerceptionSettings()).AllowedWorkspaceRoots, "perception.allowedWorkspaceRoots", errors);

        ValidateHostWorkspace((settings.Host ?? new HostSettings()).Workspace, "host.workspace", errors);

        ValidateHttps(settings.Host ?? new HostSettings(), errors);

        ValidateResilience(settings, errors);

        ValidateEventBus(settings);

        ValidateEmbeddings(settings, errors);

        ValidateScrying(settings, errors);

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
            else if (!ProviderResolver.TryResolveProviderByName(settings, embeddings.Provider, out ProviderSettings? embeddingProvider)
                || embeddingProvider is null)
            {

                errors.Add(new ConfigurationValidationError(
                    "embeddings.provider",
                    $"Arcanum:Embeddings:Provider '{embeddings.Provider}' does not match any configured provider."));

            }
            else if (embeddingProvider.Type != AiProviderKind.OpenAICompatible)
            {

                errors.Add(new ConfigurationValidationError(
                    "embeddings.provider",
                    $"Arcanum:Embeddings:Provider '{embeddings.Provider}' must be type OpenAICompatible (Ollama embeddings via /v1 with exact model names)."));

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

    /// <summary>
    /// Scrying — vision/multimodality. Validates <c>Arcanum:Scrying</c> clamp ranges (mirroring the
    /// <see cref="ValidateResilience"/> raw-vs-clamp equality pattern) and that
    /// <see cref="ScryingSettings.AllowedMimeTypes"/> is non-empty when the feature is enabled —
    /// an empty allow-list would silently reject every image even though the operator turned
    /// Scrying on.
    /// </summary>
    private static void ValidateScrying(ArcanumSettings settings, List<ConfigurationValidationError> errors)
    {

        ScryingSettings scrying = settings.Scrying ?? new ScryingSettings();

        if (scrying.MaxImageBytes != ArcanumSettingClamps.ScryingMaxImageBytes(scrying.MaxImageBytes))
        {

            errors.Add(new ConfigurationValidationError(
                "scrying.maxImageBytes",
                $"Scrying.MaxImageBytes ({scrying.MaxImageBytes}) must be within the 1,024-20,971,520 byte clamp range."));

        }

        if (scrying.MaxImagesPerRequest != ArcanumSettingClamps.ScryingMaxImagesPerRequest(scrying.MaxImagesPerRequest))
        {

            errors.Add(new ConfigurationValidationError(
                "scrying.maxImagesPerRequest",
                $"Scrying.MaxImagesPerRequest ({scrying.MaxImagesPerRequest}) must be within the 1-100 clamp range."));

        }

        if (scrying.Enabled && (scrying.AllowedMimeTypes is null || scrying.AllowedMimeTypes.Length == 0))
        {

            errors.Add(new ConfigurationValidationError(
                "scrying.allowedMimeTypes",
                "Scrying.AllowedMimeTypes must not be empty when Scrying.Enabled is true."));

        }

    }

    /// <summary>
    /// Optional HTTPS binding. When <see cref="HttpsSettings.Enabled"/> is <c>true</c>, the certificate
    /// path must be set, the TLS port must be within range and distinct from the plaintext HTTP port, and
    /// the referenced file(s) must exist on disk. All-interfaces bind (<see cref="HostSettings.ListenAny"/>
    /// / <c>ARCANUM_HOST_ANY</c>) additionally requires HTTPS enabled — plaintext any-IP HTTP is refused.
    /// No PKCS#12/PEM cryptographic load happens here — that is deferred to the Infrastructure loader at
    /// bind time — and the certificate password is never read or echoed into any error message.
    /// </summary>
    private static void ValidateHttps(HostSettings host, List<ConfigurationValidationError> errors)
    {

        HttpsSettings https = host.Https ?? new HttpsSettings();

        bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(host.ListenAny);

        if (listenAny && !https.Enabled)
        {

            errors.Add(new ConfigurationValidationError(
                "host.https.enabled",
                "Host.Https.Enabled must be true when Host.ListenAny or ARCANUM_HOST_ANY is enabled; plaintext any-IP HTTP is not permitted."));

            return;

        }

        if (!https.Enabled)
        {

            return;

        }

        if (string.IsNullOrWhiteSpace(https.CertificatePath))
        {

            errors.Add(new ConfigurationValidationError(
                "host.https.certificatePath",
                "Host.Https.CertificatePath is required when Host.Https.Enabled is true."));

        }

        if (https.Port != ArcanumSettingClamps.HostHttpsPort(https.Port))
        {

            errors.Add(new ConfigurationValidationError(
                "host.https.port",
                $"Host.Https.Port ({https.Port}) must be within the 1-65535 clamp range."));

        }

        if (https.Port == ArcanumSettingClamps.HostPort(host.Port))
        {

            errors.Add(new ConfigurationValidationError(
                "host.https.port",
                $"Host.Https.Port ({https.Port}) must differ from Host.Port ({ArcanumSettingClamps.HostPort(host.Port)}); the HTTP and HTTPS listeners cannot share a port."));

        }

        if (string.IsNullOrWhiteSpace(https.CertificatePath))
        {

            return;

        }

        string? resolvedCertificatePath = HttpsCertificatePathResolver.Resolve(https.CertificatePath);

        if (!string.IsNullOrWhiteSpace(resolvedCertificatePath) && !File.Exists(resolvedCertificatePath))
        {

            errors.Add(new ConfigurationValidationError(
                "host.https.certificatePath",
                $"Host.Https.CertificatePath '{https.CertificatePath}' does not exist or is not a file."));

        }

        if (!string.IsNullOrWhiteSpace(https.PrivateKeyPath))
        {

            string? resolvedKeyPath = HttpsCertificatePathResolver.Resolve(https.PrivateKeyPath);

            if (!string.IsNullOrWhiteSpace(resolvedKeyPath) && !File.Exists(resolvedKeyPath))
            {

                errors.Add(new ConfigurationValidationError(
                    "host.https.privateKeyPath",
                    $"Host.Https.PrivateKeyPath '{https.PrivateKeyPath}' does not exist or is not a file."));

            }

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
