using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ConfigurationValidator(
    ILogger<ConfigurationValidator>? logger = null)
{

    internal const string ObsoleteLlamaCppMigrationMessage =
        "Arcanum:LlamaCpp is no longer supported. Configure an OpenAI-compatible HTTP provider (type OpenAICompatible) instead; Ollama may use endpoint http://localhost:11434/v1.";

    internal const string ObsoleteLlamaCppServerTypeMessage =
        "Provider type LlamaCppServer is no longer supported. Configure an OpenAI-compatible HTTP provider (type OpenAICompatible) instead; Ollama may use endpoint http://localhost:11434/v1.";

    internal const string ObsoleteCacheMigrationMessage =
        "Arcanum:Cache is no longer supported. Prompt caching is selected by the built-in provider/model capability catalog.";

    internal const string ObsoleteProviderApiKeyMessage =
        "Provider ApiKey values are no longer supported in configuration. Set CredentialEnvironmentVariable to an environment-variable name, or omit it to use ARCANUM_PROVIDER_<NORMALIZED_NAME>_API_KEY.";

    internal const string ObsoleteHttpsCertificatePasswordMessage =
        "Host.Https.CertificatePassword values are no longer supported in configuration. Set CertificatePasswordEnvironmentVariable to an environment-variable name, or omit it to use ARCANUM_HTTPS_CERTIFICATE_PASSWORD.";

    internal const string ObsoleteCommLinkWebhookUrlMessage =
        "Integrations.CommLink.WebhookUrl values are no longer supported in configuration. Set WebhookUrlEnvironmentVariable to an environment-variable name, or omit it to use ARCANUM_COMMLINK_WEBHOOK_URL.";

    internal const string ObsoleteProviderCapabilityProfileMessage =
        "Provider/model tokenization and prompt-caching overrides are no longer supported. Arcanum uses its built-in capability catalog and conservative unknown-model behavior.";

    internal const string ObsoleteReasoningCapabilityMessage =
        "Per-model reasoning capability declarations (controlSupport, supportsSummary, supportsFull, supportsStreaming, reportsReasoningTokens, allowsClientOutput) are no longer supported. Arcanum derives reasoning from the declared reasoning block and wire dialect; retain only wireDialect and maxBudgetTokens for nonstandard third-party endpoints.";

    internal const string ObsoleteProviderLlamaCppMessage =
        "Provider-level llamaCpp (including modelMap) is no longer supported. List models explicitly under Providers[].Models.";

    internal const string ObsoleteProviderModelMapMessage =
        "Provider-level modelMap is no longer supported. List models explicitly under Providers[].Models.";

    internal const string ObsoleteModerationsMigrationMessage =
        "Arcanum:Moderations is no longer supported. POST /v1/moderations always returns 501 not_supported; remove the Moderations block from arcanum.json.";

    internal const string ObsoleteAutomaticAuditLookbackMessage =
        "Remove this default audit-lookback control. Audit queries automatically traverse retained dated logs and stop at the requested result page, cancellation, or the explicit from boundary.";

    internal const string ObsoleteAutomaticCodebaseIndexingMessage =
        "Remove this codebase-indexing implementation control. Watcher coalescing, reconciliation, and physical watcher admission are automatic; unfinished indexing continues in later checkpoints.";

    internal const string ObsoleteAutomaticAttachmentIndexingMessage =
        "Remove this attachment-indexing implementation control. Extraction, chunking, batching, durable reconciliation, retry progress, and model-aware retrieval are automatic.";

    internal const string ObsoleteAutomaticApprenticeQueueMessage =
        "Remove this Apprentice queue-size control. Pending starts are persisted and queue automatically under the configured concurrent-Apprentice admission policy.";

    internal const string ObsoleteAutomaticRetentionWorkMessage =
        "Remove this retention implementation control. Eligible work continues automatically through internal checkpoints until it completes or the operator cancels it.";

    private static readonly HashSet<string> ReservedRuntimeEnvironmentVariableNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ARCANUM_EDITION",
            "ARCANUM_HOST_ANY",
            "ARCANUM_LISTEN_ANY_ACK",
            "ARCANUM_ALLOW_HOST_PROCESS_TOOLS",
            "ARCANUM_SKIP_KEY_BOOTSTRAP",
            "ARCANUM_NO_COLOR",
            "ARCANUM_NO_COMMAND_CENTER",
            "ARCANUM_NO_AUTO_SERVE",
            "ARCANUM_AUTO_LAUNCHED",
            "ARCANUM_DEV_LAUNCHER",
            "ARCANUM_TEST_HOME",
            "ARCANUM_GRIMOIRE_DEV_KEY",
        };

    private static readonly (string Key, string Message)[] ObsoleteRootSections =
    [
        ("apprentices", "Apprentice enablement moved to Features and host capacity moved to Execution; workflow limits are automatic."),
        ("attachments", "Attachment enablement moved to Features; attachment storage and prompt limits are internal."),
        ("batches", "Batch concurrency moved to Execution; batch size and expiry limits are internal."),
        ("budget", "Daily budget policy moved to Cost.Budget."),
        ("campaigns", "Campaign path authority moved to Security.CampaignRoots; registry capacity is internal."),
        ("clientToolForwarding", "Client-tool enablement moved to Features.ClientTools; tool-count limits are internal."),
        ("codex", "CODEX size is now an internal host invariant."),
        ("codingTools", "Workspace-check enablement moved to Features and authored profiles moved to Integrations.WorkspaceChecks; search and patch limits are internal."),
        ("commLink", "CommLink configuration moved to Integrations.CommLink; transport limits are internal."),
        ("conclave", "Conclave and A2A opt-ins moved to Features, and A2A protocol details moved to Integrations.A2A."),
        ("embeddings", "Embedding opt-ins moved to Features and provider/model facts moved to Integrations.Embeddings; retrieval tuning is internal."),
        ("eventBus", "SSE capacity moved to Execution; channel and heartbeat behavior are internal."),
        ("files", "Upload MIME policy moved to Security.AllowedUploadMimeTypes; upload limits are internal."),
        ("grimoire", "Grimoire hydration, retention, and paging limits are internal."),
        ("guardrails", "Guardrail enablement moved to Features and policy content moved to Security.Guardrails."),
        ("intelligence", "Intelligence routing, context, retry, and turn mechanics are automatic or internal."),
        ("logs", "The in-memory log level moved to Host.MinLogLevelInBuffer; buffer capacity is internal."),
        ("mcp", "The plaintext-host allowlist moved to Integrations.Mcp; MCP transport limits are internal."),
        ("metrics", "Metrics enablement moved to Features.Metrics and authentication policy moved to Security.MetricsRequireApiKey."),
        ("perception", "Perception path authority moved to Security.PerceptionWorkspaceRoots; traversal limits are internal."),
        ("pricing", "Provider pricing moved to Cost.Pricing."),
        ("prompts", "Prompt rendering limits are internal."),
        ("provingGrounds", "Proving Grounds execution follows caller cancellation and has no configuration block."),
        ("resilience", "Provider health and fallback behavior are automatic."),
        ("scrying", "Scrying enablement moved to Features.Scrying and MIME policy moved to Security.AllowedImageMimeTypes; image limits are internal."),
        ("server", "The PID path is derived from ArcanumPaths."),
        ("sessions", "Memory-management enablement moved to Features.MemoryManagement; session storage limits are internal."),
        ("spells", "Spell path authority moved to Security.SpellWorkspaceRoots; file and resonance limits are internal."),
        ("structuredOutput", "Structured output validation and provider-constrained decoding are automatic."),
        ("ward", "Ward policy moved to Security.Ward; approval capacity and timeout are internal."),
        ("webBrowsing", "Web-tool enablement moved to Features.WebBrowsing and provider facts moved to Integrations.WebResearch; fetch and output limits are internal."),
    ];

    private static readonly (string Path, string Message)[] ObsoleteInternalWorkflowPaths =
    [
        ("host.auditLog.retentionDays", ObsoleteAutomaticAuditLookbackMessage),
        ("security.guardrails.auditLog.retentionDays", ObsoleteAutomaticAuditLookbackMessage),
        ("integrations.embeddings.codebaseIndexing.watcherDebounceMilliseconds", ObsoleteAutomaticCodebaseIndexingMessage),
        ("integrations.embeddings.codebaseIndexing.maxWatchers", ObsoleteAutomaticCodebaseIndexingMessage),
        ("integrations.embeddings.codebaseIndexing.reconciliationIntervalMinutes", ObsoleteAutomaticCodebaseIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxAttachmentBytes", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxExtractedCharacters", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.chunkSizeCharacters", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.chunkOverlapCharacters", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxChunksPerAttachment", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxAttachmentsPerBatch", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.queueCapacity", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxRetries", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.retryDelaySeconds", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.processingTimeoutSeconds", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxRetrievedChunks", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxRetrievedAttachments", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxRetrievedBytes", ObsoleteAutomaticAttachmentIndexingMessage),
        ("integrations.embeddings.attachmentIndexing.maxRetrievedTokens", ObsoleteAutomaticAttachmentIndexingMessage),
        ("execution.maxPendingApprenticeStarts", ObsoleteAutomaticApprenticeQueueMessage),
        ("retention.maxItemsPerSweep", ObsoleteAutomaticRetentionWorkMessage),
        ("retention.checkpointInterval", ObsoleteAutomaticRetentionWorkMessage),
    ];

    /// <summary>
    /// Walks the complete <c>Arcanum</c> configuration subtree against source-generated JSON
    /// metadata before options binding. Unknown paths fail closed; selected removed paths retain
    /// actionable migration messages, and all issues are returned together.
    /// </summary>
    public Result RejectObsoleteKeys(IConfiguration configuration)
    {

        List<ConfigurationValidationError> errors = [];

        IConfigurationSection arcanum = configuration.GetSection("Arcanum");

        foreach ((string key, string message) in ObsoleteRootSections)
        {
            if (arcanum.GetSection(key).Exists())
            {
                errors.Add(new ConfigurationValidationError(key, message));
            }
        }

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

        if (arcanum.GetSection("Moderations").Exists())
        {
            errors.Add(new ConfigurationValidationError(
                "moderations",
                ObsoleteModerationsMigrationMessage));
        }

        foreach ((string path, string message) in ObsoleteInternalWorkflowPaths)
        {

            if (arcanum.GetSection(path.Replace('.', ':')).Exists())
            {

                AddErrorIfMissing(
                    new ConfigurationValidationError(path, message),
                    errors);

            }

        }

        if (arcanum.GetSection("Host:Https:CertificatePassword").Exists())
        {
            errors.Add(new ConfigurationValidationError(
                "host.https.certificatePassword",
                ObsoleteHttpsCertificatePasswordMessage));
        }

        if (arcanum.GetSection("Integrations:CommLink:WebhookUrl").Exists())
        {
            errors.Add(new ConfigurationValidationError(
                "integrations.commLink.webhookUrl",
                ObsoleteCommLinkWebhookUrlMessage));
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

            if (provider.GetSection("ApiKey").Exists())
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.apiKey",
                    ObsoleteProviderApiKeyMessage));
            }

            foreach (string removedCapability in new[]
                     {
                         "Tokenization",
                         "PromptCaching",
                         "SupportsPromptCaching",
                     })
            {
                if (provider.GetSection(removedCapability).Exists())
                {
                    errors.Add(new ConfigurationValidationError(
                        $"{pointer}.{char.ToLowerInvariant(removedCapability[0]) + removedCapability[1..]}",
                        ObsoleteProviderCapabilityProfileMessage));
                }
            }

            string? typeValue = provider["Type"] ?? provider["type"];

            if (IsObsoleteLlamaCppServerType(typeValue))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.type",
                    ObsoleteLlamaCppServerTypeMessage));
            }

            foreach (IConfigurationSection model in provider.GetSection("Models").GetChildren())
            {
                string modelPointer = string.IsNullOrEmpty(model.Key)
                    ? $"{pointer}.models"
                    : $"{pointer}.models[{model.Key}]";

                foreach (string removedCapability in new[] { "Tokenization", "PromptCaching" })
                {
                    if (model.GetSection(removedCapability).Exists())
                    {
                        errors.Add(new ConfigurationValidationError(
                            $"{modelPointer}.{char.ToLowerInvariant(removedCapability[0]) + removedCapability[1..]}",
                            ObsoleteProviderCapabilityProfileMessage));
                    }
                }

                foreach (string removedReasoningFact in new[]
                {
                    "ControlSupport",
                    "SupportsSummary",
                    "SupportsFull",
                    "SupportsStreaming",
                    "ReportsReasoningTokens",
                    "AllowsClientOutput",
                })
                {
                    if (model.GetSection("Reasoning").GetSection(removedReasoningFact).Exists())
                    {
                        errors.Add(new ConfigurationValidationError(
                            $"{modelPointer}.reasoning.{char.ToLowerInvariant(removedReasoningFact[0]) + removedReasoningFact[1..]}",
                            ObsoleteReasoningCapabilityMessage));
                    }
                }
            }
        }

        ValidateUnknownConfigurationChildren(
            arcanum,
            typeof(ArcanumSettings),
            string.Empty,
            errors);

        return BuildRawTreeValidationResult(errors);

    }

    /// <summary>
    /// Walks an <see cref="ArcanumSettings"/>-shaped API body against the complete source-generated
    /// configuration schema before deserialization. Unknown and selected obsolete paths are grouped;
    /// obsolete provider types retain migration-specific errors.
    /// </summary>
    public Result RejectObsoleteJsonKeys(JsonElement root)
    {

        List<ConfigurationValidationError> errors = [];

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ConfigurationValidationError(
                "$",
                "Configuration root must be a JSON object."));

            return BuildRawTreeValidationResult(errors);
        }

        foreach ((string key, string message) in ObsoleteRootSections)
        {
            if (TryGetPropertyIgnoreCase(root, key, out _))
            {
                errors.Add(new ConfigurationValidationError(key, message));
            }
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

        if (TryGetPropertyIgnoreCase(root, "moderations", out _))
        {
            errors.Add(new ConfigurationValidationError(
                "moderations",
                ObsoleteModerationsMigrationMessage));
        }

        foreach ((string path, string message) in ObsoleteInternalWorkflowPaths)
        {

            if (TryGetNestedPropertyIgnoreCase(root, path, out _))
            {

                AddErrorIfMissing(
                    new ConfigurationValidationError(path, message),
                    errors);

            }

        }

        if (TryGetPropertyIgnoreCase(root, "host", out JsonElement host)
            && host.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(host, "https", out JsonElement https)
            && https.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(https, "certificatePassword", out _))
        {
            errors.Add(new ConfigurationValidationError(
                "host.https.certificatePassword",
                ObsoleteHttpsCertificatePasswordMessage));
        }

        if (TryGetPropertyIgnoreCase(root, "integrations", out JsonElement integrations)
            && integrations.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(integrations, "commLink", out JsonElement commLink)
            && commLink.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(commLink, "webhookUrl", out _))
        {
            errors.Add(new ConfigurationValidationError(
                "integrations.commLink.webhookUrl",
                ObsoleteCommLinkWebhookUrlMessage));
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

                if (TryGetPropertyIgnoreCase(provider, "apiKey", out _))
                {
                    errors.Add(new ConfigurationValidationError(
                        $"providers[{index}].apiKey",
                        ObsoleteProviderApiKeyMessage));
                }

                foreach (string removedCapability in new[]
                         {
                             "tokenization",
                             "promptCaching",
                             "supportsPromptCaching",
                         })
                {
                    if (TryGetPropertyIgnoreCase(provider, removedCapability, out _))
                    {
                        errors.Add(new ConfigurationValidationError(
                            $"providers[{index}].{removedCapability}",
                            ObsoleteProviderCapabilityProfileMessage));
                    }
                }

                if (TryGetPropertyIgnoreCase(provider, "type", out JsonElement typeElement)
                    && typeElement.ValueKind == JsonValueKind.String
                    && IsObsoleteLlamaCppServerType(typeElement.GetString()))
                {
                    errors.Add(new ConfigurationValidationError(
                        $"providers[{index}].type",
                        ObsoleteLlamaCppServerTypeMessage));
                }

                if (TryGetPropertyIgnoreCase(provider, "models", out JsonElement models)
                    && models.ValueKind == JsonValueKind.Array)
                {
                    int modelIndex = 0;

                    foreach (JsonElement model in models.EnumerateArray())
                    {
                        if (model.ValueKind == JsonValueKind.Object)
                        {
                            foreach (string removedCapability in new[]
                                     {
                                         "tokenization",
                                         "promptCaching",
                                     })
                            {
                                if (TryGetPropertyIgnoreCase(model, removedCapability, out _))
                                {
                                    errors.Add(new ConfigurationValidationError(
                                        $"providers[{index}].models[{modelIndex}].{removedCapability}",
                                        ObsoleteProviderCapabilityProfileMessage));
                                }
                            }
                        }

                        modelIndex++;
                    }
                }

                index++;
            }
        }

        ValidateUnknownJsonProperties(
            root,
            typeof(ArcanumSettings),
            string.Empty,
            errors);

        return BuildRawTreeValidationResult(errors);

    }

    /// <summary>
    /// Validates the persisted <c>arcanum.json</c> wrapper before deserialization. The file must
    /// contain exactly one retained root, <c>Arcanum</c>, whose value is validated with the same
    /// fail-closed schema used by API configuration bodies.
    /// </summary>
    public Result ValidateConfigurationFileJson(JsonElement root)
    {
        List<ConfigurationValidationError> errors = [];

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ConfigurationValidationError(
                "$",
                "arcanum.json root must be a JSON object containing an Arcanum property."));

            return BuildRawTreeValidationResult(errors);
        }

        JsonElement arcanum = default;
        bool foundArcanum = false;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "Arcanum", StringComparison.Ordinal))
            {
                if (!foundArcanum)
                {
                    arcanum = property.Value;
                    foundArcanum = true;
                }

                continue;
            }

            AddUnknownPath(property.Name, errors);
        }

        if (!foundArcanum)
        {
            errors.Add(new ConfigurationValidationError(
                "Arcanum",
                "arcanum.json must contain the Arcanum root object."));
        }
        else
        {
            Result nested = RejectObsoleteJsonKeys(arcanum);

            if (nested.IsFailure && nested.Error.Details is { Count: > 0 } details)
            {
                foreach (ConfigurationValidationError detail in details)
                {
                    AddErrorIfMissing(detail, errors);
                }
            }
        }

        return BuildRawTreeValidationResult(errors);
    }

    private const string UnknownConfigurationPathMessage =
        "Unknown or obsolete configuration path. Remove it or use a retained Arcanum configuration property.";

    private static Result BuildRawTreeValidationResult(
        List<ConfigurationValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return Result.Success();
        }

        errors.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Pointer, right.Pointer));

        return Result.Failure(new Error(
            "Configuration.ValidationFailed",
            $"{errors.Count} configuration issue(s).",
            errors));
    }

    private static void ValidateUnknownConfigurationChildren(
        IConfigurationSection section,
        Type expectedType,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        expectedType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;

        if (TryGetDictionaryValueType(expectedType, out Type? valueType))
        {
            foreach (IConfigurationSection child in section.GetChildren())
            {
                if (IsObjectConfigurationType(valueType))
                {
                    ValidateUnknownConfigurationDictionaryEntry(
                        child,
                        valueType,
                        pointer,
                        child.Key,
                        errors);
                }
                else
                {
                    ValidateUnknownConfigurationChildren(
                        child,
                        valueType,
                        AppendDynamicKey(pointer, child.Key),
                        errors);
                }
            }

            return;
        }

        if (TryGetEnumerableElementType(expectedType, out Type? elementType))
        {
            foreach (IConfigurationSection child in section.GetChildren())
            {
                if (!int.TryParse(child.Key, out int index) || index < 0)
                {
                    AddUnknownPath(AppendProperty(pointer, child.Key), errors);
                    continue;
                }

                ValidateUnknownConfigurationChildren(
                    child,
                    elementType,
                    AppendDynamicKey(pointer, child.Key),
                    errors);
            }

            return;
        }

        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (!TryResolveConfigurationProperty(
                    expectedType,
                    child.Key,
                    out string propertyName,
                    out Type propertyType))
            {
                AddUnknownPath(AppendProperty(pointer, child.Key), errors);
                continue;
            }

            ValidateUnknownConfigurationChildren(
                child,
                propertyType,
                AppendProperty(pointer, propertyName),
                errors);
        }
    }

    private static void ValidateUnknownConfigurationDictionaryEntry(
        IConfigurationSection section,
        Type valueType,
        string dictionaryPointer,
        string dynamicKey,
        List<ConfigurationValidationError> errors)
    {
        IConfigurationSection[] children = section.GetChildren().ToArray();
        string entryPointer = AppendDynamicKey(dictionaryPointer, dynamicKey);

        if (children.Length == 0
            || children.Any(child => TryResolveConfigurationProperty(
                valueType,
                child.Key,
                out _,
                out _)))
        {
            ValidateUnknownConfigurationChildren(
                section,
                valueType,
                entryPointer,
                errors);
            return;
        }

        // IConfiguration flattens ':' inside JSON dictionary keys into path segments. Continue
        // through object-shaped segments only when they lead to a real value property; scalar
        // unknown children remain ordinary schema errors.
        foreach (IConfigurationSection child in children)
        {
            if (child.GetChildren().Any())
            {
                ValidateUnknownConfigurationDictionaryEntry(
                    child,
                    valueType,
                    dictionaryPointer,
                    $"{dynamicKey}:{child.Key}",
                    errors);
            }
            else
            {
                AddUnknownPath(AppendProperty(entryPointer, child.Key), errors);
            }
        }
    }

    private static bool IsObjectConfigurationType(Type type)
    {
        if (type == typeof(ModelEntry))
        {
            return true;
        }

        JsonTypeInfo? typeInfo = ConfigurationJsonContext.Default.GetTypeInfo(type);

        return typeInfo?.Kind == JsonTypeInfoKind.Object;
    }

    private static void ValidateUnknownJsonProperties(
        JsonElement element,
        Type expectedType,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        expectedType = Nullable.GetUnderlyingType(expectedType) ?? expectedType;

        if (TryGetDictionaryValueType(expectedType, out Type? valueType))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    ValidateUnknownJsonProperties(
                        property.Value,
                        valueType,
                        AppendDynamicKey(pointer, property.Name),
                        errors);
                }
            }

            return;
        }

        if (TryGetEnumerableElementType(expectedType, out Type? elementType))
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;

                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateUnknownJsonProperties(
                        item,
                        elementType,
                        AppendDynamicKey(pointer, index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        errors);
                    index++;
                }
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!TryResolveConfigurationProperty(
                    expectedType,
                    property.Name,
                    out string propertyName,
                    out Type propertyType))
            {
                AddUnknownPath(AppendProperty(pointer, property.Name), errors);
                continue;
            }

            ValidateUnknownJsonProperties(
                property.Value,
                propertyType,
                AppendProperty(pointer, propertyName),
                errors);
        }
    }

    private static bool TryResolveConfigurationProperty(
        Type expectedType,
        string suppliedName,
        out string propertyName,
        out Type propertyType)
    {
        if (expectedType == typeof(ModelEntry))
        {
            if (string.Equals(suppliedName, "name", StringComparison.OrdinalIgnoreCase))
            {
                propertyName = "name";
                propertyType = typeof(string);
                return true;
            }

            if (string.Equals(suppliedName, "supportsVision", StringComparison.OrdinalIgnoreCase))
            {
                propertyName = "supportsVision";
                propertyType = typeof(bool);
                return true;
            }

            if (string.Equals(suppliedName, "reasoning", StringComparison.OrdinalIgnoreCase))
            {
                // The JSON reasoning block keeps the historical ReasoningCapabilities shape: removed
                // facts (controlSupport, supportsSummary, …) stay "known" here so they are reported by
                // the precise obsolete-key check against IConfiguration instead of as unknown paths.
                propertyName = "reasoning";
                propertyType = typeof(ReasoningCapabilities);
                return true;
            }

            propertyName = string.Empty;
            propertyType = typeof(object);
            return false;
        }

        JsonTypeInfo? typeInfo = ConfigurationJsonContext.Default.GetTypeInfo(expectedType);

        if (typeInfo is null || typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            propertyName = string.Empty;
            propertyType = typeof(object);
            return false;
        }

        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (string.Equals(
                    property.Name,
                    suppliedName,
                    StringComparison.OrdinalIgnoreCase))
            {
                propertyName = property.Name;
                propertyType = property.PropertyType;
                return true;
            }
        }

        propertyName = string.Empty;
        propertyType = typeof(object);
        return false;
    }

    private static bool TryGetDictionaryValueType(
        Type type,
        out Type valueType)
    {
        JsonTypeInfo? typeInfo = ConfigurationJsonContext.Default.GetTypeInfo(type);

        if (typeInfo is { Kind: JsonTypeInfoKind.Dictionary, ElementType: not null })
        {
            valueType = typeInfo.ElementType;
            return true;
        }

        valueType = typeof(object);
        return false;
    }

    private static bool TryGetEnumerableElementType(
        Type type,
        out Type elementType)
    {
        JsonTypeInfo? typeInfo = ConfigurationJsonContext.Default.GetTypeInfo(type);

        if (typeInfo is { Kind: JsonTypeInfoKind.Enumerable, ElementType: not null })
        {
            elementType = typeInfo.ElementType;
            return true;
        }

        elementType = typeof(object);
        return false;
    }

    private static string AppendProperty(string pointer, string propertyName) =>
        string.IsNullOrEmpty(pointer)
            ? propertyName
            : $"{pointer}.{propertyName}";

    private static string AppendDynamicKey(string pointer, string key) =>
        $"{pointer}[{key}]";

    private static void AddUnknownPath(
        string pointer,
        List<ConfigurationValidationError> errors) =>
        AddErrorIfMissing(
            new ConfigurationValidationError(
                string.IsNullOrEmpty(pointer) ? "$" : pointer,
                UnknownConfigurationPathMessage),
            errors);

    private static void AddErrorIfMissing(
        ConfigurationValidationError candidate,
        List<ConfigurationValidationError> errors)
    {
        if (errors.Any(error => string.Equals(
                error.Pointer,
                candidate.Pointer,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        errors.Add(candidate);
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

    private static bool TryGetNestedPropertyIgnoreCase(
        JsonElement root,
        string path,
        out JsonElement value)
    {

        value = root;

        foreach (string segment in path.Split('.'))
        {

            if (value.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(value, segment, out value))
            {

                value = default;

                return false;

            }

        }

        return true;

    }

    public Result Validate(ArcanumSettings settings)
    {

        List<ConfigurationValidationError> errors = [];

        ProviderSettings[] providers = settings.Providers ?? [];

        HashSet<string> seenProviderNames = new(StringComparer.OrdinalIgnoreCase);
        List<EnvironmentVariableReference> environmentReferences = [];

        for (int providerIndex = 0; providerIndex < providers.Length; providerIndex++)
        {

            ProviderSettings provider = providers[providerIndex];

            string providerPointer = $"providers[{providerIndex}]";

            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{providerPointer}.name",
                    "Provider Name must not be blank."));
            }

            // Missing Type defaults to OpenAICompatible (enum zero). Reject undefined numeric
            // leftovers; do not require Type to be present.
            if (!Enum.IsDefined(provider.Type))
            {

                errors.Add(new ConfigurationValidationError(
                    $"{providerPointer}.type",
                    $"Provider '{provider.Name}' type must be OpenAICompatible (Ollama via http://localhost:11434/v1), ClaudeCodeCli, or CodexCli."));

            }

            bool isFamiliar = FamiliarProviders.IsFamiliar(provider.Type);

            IReadOnlyList<ModelEntry> models = provider.Models ?? [];

            if (isFamiliar)
            {

                ValidateFamiliarProvider(provider, providerPointer, errors);

            }
            else
            {

                ValidateHttpProviderOnlyFields(provider, providerPointer, errors);

                ValidateOptionalEnvironmentVariableName(
                    provider.CredentialEnvironmentVariable,
                    $"{providerPointer}.credentialEnvironmentVariable",
                    errors);
                AddEnvironmentVariableReference(
                    EnvironmentCredentialResolver.GetProviderApiKeyEnvironmentVariableName(provider),
                    $"{providerPointer}.credentialEnvironmentVariable",
                    environmentReferences);

                // A Familiar's catalogue belongs to the vendor, not to arcanum.json — requiring a
                // models array here would defeat the automatic availability the kind exists for.
                if (models.Count == 0)
                {

                    errors.Add(new ConfigurationValidationError(
                        providerPointer,
                        $"Provider '{provider.Name}' has no configured models."));

                }

            }

            for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                ValidateReasoningFacts(
                    models[modelIndex].Reasoning,
                    $"{providerPointer}.models[{modelIndex}].reasoning",
                    errors);
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
                    $"Provider '{provider.Name}' endpoint must be an absolute http or https URI."));

            }

            if (!string.IsNullOrWhiteSpace(provider.Name)
                && !seenProviderNames.Add(provider.Name.Trim()))
            {

                // Provider health is keyed by Name (ProviderHealthTracker), and ProviderResolver's
                // by-name lookups (TryResolveProviderByName, used by embeddings config) return only
                // the first match — a duplicate name would otherwise silently share health state and
                // resolve to the wrong provider for the second entry.
                errors.Add(new ConfigurationValidationError(
                    $"{providerPointer}.name",
                    $"Provider name '{provider.Name}' is configured more than once; provider names must be unique."));

            }

        }

        HttpsSettings https = settings.Host?.Https ?? new HttpsSettings();
        CommLinkSettings commLink = settings.ResolveCommLink();
        WebResearchIntegrationSettings webResearch =
            settings.Integrations?.WebResearch ?? new WebResearchIntegrationSettings();
        WebBrowsingSettings webBrowsing = settings.ResolveWebBrowsing();

        AddEnvironmentVariableReference(
            EnvironmentCredentialResolver.GetHttpsCertificatePasswordEnvironmentVariableName(https),
            "host.https.certificatePasswordEnvironmentVariable",
            environmentReferences);
        ValidateOptionalEnvironmentVariableName(
            commLink.WebhookUrlEnvironmentVariable,
            "integrations.commLink.webhookUrlEnvironmentVariable",
            errors);
        AddEnvironmentVariableReference(
            EnvironmentCredentialResolver.GetCommLinkWebhookUrlEnvironmentVariableName(commLink),
            "integrations.commLink.webhookUrlEnvironmentVariable",
            environmentReferences);
        ValidateOptionalEnvironmentVariableName(
            webResearch.CredentialEnvironmentVariable,
            "integrations.webResearch.credentialEnvironmentVariable",
            errors);
        AddEnvironmentVariableReference(
            EnvironmentCredentialResolver.GetWebResearchApiKeyEnvironmentVariableName(webBrowsing),
            "integrations.webResearch.credentialEnvironmentVariable",
            environmentReferences);
        ConclaveA2ASettings a2a = settings.ResolveA2A();
        ValidateOptionalEnvironmentVariableName(
            a2a.OutboundCredentialEnvironmentVariable,
            "integrations.a2A.outboundCredentialEnvironmentVariable",
            errors);
        if (!string.IsNullOrWhiteSpace(a2a.OutboundCredentialEnvironmentVariable))
        {

            AddEnvironmentVariableReference(
                a2a.OutboundCredentialEnvironmentVariable.Trim(),
                "integrations.a2A.outboundCredentialEnvironmentVariable",
                environmentReferences);

        }
        ValidateA2A(a2a, errors);
        ValidateWebResearch(webResearch, errors);
        ValidateUniqueEnvironmentVariableReferences(environmentReferences, errors);

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

        ValidatePricing(settings.ResolvePricing(), errors);

        ValidateCodingTools(settings, errors);

        ValidateWardAutoApproval(settings.Security?.Ward, errors);

        ValidateDaemonJobs(settings.Daemon, errors);

        ValidatePathAllowlist(settings.ResolveCampaignRoots(), "security.campaignRoots", errors);

        ValidatePathAllowlist(settings.ResolveSpellRoots(), "security.spellWorkspaceRoots", errors);

        ValidatePathAllowlist(settings.ResolvePerceptionRoots(), "security.perceptionWorkspaceRoots", errors);

        ValidateHostWorkspace(settings.ResolveDefaultWorkspace(), "workspaces.defaultRoot", errors);

        ValidateHttps(settings.Host ?? new HostSettings(), errors);

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

    /// <summary>
    /// The Ward auto-approval allowlist grants advance operator consent, so a typo must fail at
    /// startup rather than silently never matching. Tool availability is a runtime fact (MCP servers
    /// are discovered after configuration binds), so only the shape is checked here: a blank entry is
    /// meaningless and a duplicate hides an intent the operator cannot see in the file. A name that
    /// resolves to no available tool simply grants nothing.
    /// </summary>
    private static void ValidateWardAutoApproval(
        WardPolicySettings? ward,
        List<ConfigurationValidationError> errors)
    {

        List<string> tools = ward?.AutoApprove?.Tools ?? [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < tools.Count; index++)
        {

            string pointer = $"security.ward.autoApprove.tools[{index}]";

            string? tool = tools[index];

            if (string.IsNullOrWhiteSpace(tool))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    "Ward auto-approval tool names must not be blank."));

                continue;

            }

            if (!seen.Add(tool.Trim()))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Ward auto-approval tool '{tool.Trim()}' is listed more than once; names must be unique."));

            }

        }

    }

    /// <summary>
    /// A scheduled job's daemon id is its name verbatim (<c>unseen-servant:{Name}</c>), and the
    /// daemon registry resolves an id to the first match, so a blank or duplicate name produces a
    /// job that still ticks and still spends tokens but can never be run on demand or inspected —
    /// its history is attributed to its twin. Interval bounds are deliberately left alone: they are
    /// clamped by <c>ArcanumSettingClamps.UnseenServantIntervalMinutes</c>, not rejected.
    /// </summary>
    private static void ValidateDaemonJobs(
        DaemonSettings? daemon,
        List<ConfigurationValidationError> errors)
    {

        List<UnseenServantJob> jobs = daemon?.Jobs ?? [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < jobs.Count; index++)
        {

            string pointer = $"daemon.jobs[{index}].name";

            string? name = jobs[index]?.Name;

            if (string.IsNullOrWhiteSpace(name))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    "Unseen Servant job names must not be blank."));

                continue;

            }

            if (!seen.Add(name.Trim()))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Unseen Servant job name '{name.Trim()}' is configured more than once; job names must be unique."));

            }

        }

    }

    private static void ValidateCodingTools(
        ArcanumSettings settings,
        List<ConfigurationValidationError> errors)
    {
        WorkspaceCheckSettings check = settings.ResolveWorkspaceChecks();

        ValidateWorkspaceCheckExecutable(
            check.ExecutableCatalog?.DotNet,
            settings.ResolveDefaultWorkspace(),
            errors);
        ValidateWorkspaceCheckProfiles(check, errors);
    }

    private static void ValidateWorkspaceCheckProfiles(
        WorkspaceCheckSettings check,
        List<ConfigurationValidationError> errors)
    {
        Dictionary<string, WorkspaceCheckProfileSettings> profiles =
            check.CustomProfiles ?? new Dictionary<string, WorkspaceCheckProfileSettings>();
        int maxProfiles = ArcanumSettingClamps.WorkspaceCheckMaxCustomProfiles(
            ArcanumRuntimeDefaults.WorkspaceChecks.MaxCustomProfiles);

        if (profiles.Count > maxProfiles)
        {
            errors.Add(new ConfigurationValidationError(
                "integrations.workspaceChecks.customProfiles",
                $"WorkspaceCheck.CustomProfiles contains {profiles.Count} entries; the internal limit is {maxProfiles}."));
        }

        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string profileId, WorkspaceCheckProfileSettings? profile) in profiles)
        {
            string pointer = $"integrations.workspaceChecks.customProfiles[{profileId}]";

            if (!seenIds.Add(profileId))
            {
                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Workspace-check profile ID '{profileId}' is duplicated case-insensitively."));
            }

            if (!IsValidProfileId(profileId))
            {
                errors.Add(new ConfigurationValidationError(
                    pointer,
                    "Workspace-check profile IDs must be 1-64 lowercase ASCII letters, digits, or hyphens, and must start with a letter or digit."));
            }

            if (WorkspaceCheckCatalogDefaults.ReservedProfileIds.Contains(profileId))
            {
                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Workspace-check profile ID '{profileId}' is reserved for an immutable built-in profile."));
            }

            if (profile is null)
            {
                errors.Add(new ConfigurationValidationError(pointer, "Workspace-check profile must be an object."));
                continue;
            }

            if (!string.Equals(
                    profile.ExecutableId,
                    WorkspaceCheckCatalogDefaults.DotNetExecutableId,
                    StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.executableId",
                    $"Workspace-check profile executable '{profile.ExecutableId}' is not in the closed executable catalog."));
            }

            if (!Enum.IsDefined(profile.Kind))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.kind",
                    $"Workspace-check profile kind '{profile.Kind}' is not defined."));
            }

            if (!Enum.IsDefined(profile.Parser))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.parser",
                    $"Workspace-check diagnostic parser '{profile.Parser}' is not defined."));
            }

            ValidateWorkspaceCheckProfileShape(profileId, profile, pointer, check, errors);
        }
    }

    private static void ValidateWorkspaceCheckProfileShape(
        string profileId,
        WorkspaceCheckProfileSettings profile,
        string pointer,
        WorkspaceCheckSettings check,
        List<ConfigurationValidationError> errors)
    {
        string[] fixedArguments = profile.FixedArguments ?? [];
        int maxFixedArguments = ArcanumSettingClamps.WorkspaceCheckMaxFixedArgumentsPerProfile(
            ArcanumRuntimeDefaults.WorkspaceChecks.MaxFixedArgumentsPerProfile);
        int maxTokenChars = ArcanumSettingClamps.WorkspaceCheckMaxArgumentTokenChars(
            ArcanumRuntimeDefaults.WorkspaceChecks.MaxArgumentTokenChars);

        if (fixedArguments.Length == 0)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.fixedArguments",
                $"Workspace-check profile '{profileId}' must declare its closed dotnet subcommand."));
        }
        else if (fixedArguments.Length > maxFixedArguments)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.fixedArguments",
                $"Workspace-check profile '{profileId}' has {fixedArguments.Length} fixed arguments; the internal limit is {maxFixedArguments}."));
        }

        for (int i = 0; i < fixedArguments.Length; i++)
        {
            ValidateWorkspaceCheckArgumentToken(
                fixedArguments[i],
                $"{pointer}.fixedArguments[{i}]",
                maxTokenChars,
                errors);
        }

        if (!string.IsNullOrWhiteSpace(profile.Target)
            && !IsValidWorkspaceCheckTarget(
                profile.Target,
                maxTokenChars))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.target",
                "Workspace-check target must be a bounded workspace-relative .sln, .slnx, .csproj, .fsproj, or .vbproj path."));
        }

        if (Enum.IsDefined(profile.Kind)
            && fixedArguments.Length > 0
            && !ProfileSubcommandMatchesKind(profile.Kind, fixedArguments))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.fixedArguments",
                $"Workspace-check profile '{profileId}' fixed arguments do not match its closed '{profile.Kind}' kind."));
        }

        if (Enum.IsDefined(profile.Kind)
            && Enum.IsDefined(profile.Parser)
            && !ParserMatchesKind(profile.Kind, profile.Parser))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.parser",
                $"Workspace-check parser '{profile.Parser}' does not match profile kind '{profile.Kind}'."));
        }

        Dictionary<string, WorkspaceCheckProfileOptionSettings> options =
            profile.Options ?? new Dictionary<string, WorkspaceCheckProfileOptionSettings>();
        int maxOptions = ArcanumSettingClamps.WorkspaceCheckMaxOptionsPerProfile(
            ArcanumRuntimeDefaults.WorkspaceChecks.MaxOptionsPerProfile);

        if (options.Count > maxOptions)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.options",
                $"Workspace-check profile '{profileId}' has {options.Count} options; the internal limit is {maxOptions}."));
        }

        HashSet<string> seenOptions = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string optionId, WorkspaceCheckProfileOptionSettings? option) in options)
        {
            string optionPointer = $"{pointer}.options[{optionId}]";

            if (!seenOptions.Add(optionId))
            {
                errors.Add(new ConfigurationValidationError(
                    optionPointer,
                    $"Workspace-check option ID '{optionId}' is duplicated case-insensitively."));
            }

            if (!IsValidProfileId(optionId))
            {
                errors.Add(new ConfigurationValidationError(
                    optionPointer,
                    "Workspace-check option IDs must use the same lowercase ASCII ID format as profile IDs."));
            }

            if (option is null)
            {
                errors.Add(new ConfigurationValidationError(optionPointer, "Workspace-check option must be an object."));
                continue;
            }

            ValidateWorkspaceCheckAllowedValues(
                option.AllowedValues,
                optionPointer,
                check,
                errors);
        }
    }

    private static void ValidateWorkspaceCheckAllowedValues(
        Dictionary<string, string[]>? allowedValues,
        string optionPointer,
        WorkspaceCheckSettings check,
        List<ConfigurationValidationError> errors)
    {
        Dictionary<string, string[]> values = allowedValues ?? new Dictionary<string, string[]>();
        int maxValues = ArcanumSettingClamps.WorkspaceCheckMaxAllowedValuesPerOption(
            ArcanumRuntimeDefaults.WorkspaceChecks.MaxAllowedValuesPerOption);
        int maxTokens = ArcanumSettingClamps.WorkspaceCheckMaxFixedArgumentsPerProfile(
            ArcanumRuntimeDefaults.WorkspaceChecks.MaxFixedArgumentsPerProfile);
        int maxTokenChars = ArcanumSettingClamps.WorkspaceCheckMaxArgumentTokenChars(
            ArcanumRuntimeDefaults.WorkspaceChecks.MaxArgumentTokenChars);

        if (values.Count == 0)
        {
            errors.Add(new ConfigurationValidationError(
                $"{optionPointer}.allowedValues",
                "Workspace-check options must allow at least one exact value rendering."));
            return;
        }

        if (values.Count > maxValues)
        {
            errors.Add(new ConfigurationValidationError(
                $"{optionPointer}.allowedValues",
                $"Workspace-check option has {values.Count} allowed values; the internal limit is {maxValues}."));
        }

        HashSet<string> seenValues = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string valueId, string[]? rendering) in values)
        {
            string valuePointer = $"{optionPointer}.allowedValues[{valueId}]";

            if (!seenValues.Add(valueId))
            {
                errors.Add(new ConfigurationValidationError(
                    valuePointer,
                    $"Workspace-check option value '{valueId}' is duplicated case-insensitively."));
            }

            if (string.IsNullOrWhiteSpace(valueId) || valueId.Length > maxTokenChars)
            {
                errors.Add(new ConfigurationValidationError(
                    valuePointer,
                    $"Workspace-check option values must be non-empty and at most {maxTokenChars} characters."));
            }

            string[] tokens = rendering ?? [];

            if (tokens.Length == 0 || tokens.Length > maxTokens)
            {
                errors.Add(new ConfigurationValidationError(
                    valuePointer,
                    $"Workspace-check option rendering must contain 1-{maxTokens} exact argument tokens."));
                continue;
            }

            for (int i = 0; i < tokens.Length; i++)
            {
                ValidateWorkspaceCheckArgumentToken(
                    tokens[i],
                    $"{valuePointer}[{i}]",
                    maxTokenChars,
                    errors);
            }
        }
    }

    private static void ValidateWorkspaceCheckArgumentToken(
        string? token,
        string pointer,
        int maxTokenChars,
        List<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > maxTokenChars
            || token.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            errors.Add(new ConfigurationValidationError(
                pointer,
                $"Workspace-check argument tokens must be non-empty, single-line, and at most {maxTokenChars} characters."));
            return;
        }

        if (token[0] == '@' || IsScriptPath(token))
        {
            errors.Add(new ConfigurationValidationError(
                pointer,
                "Workspace-check profiles cannot invoke response files, scripts, shells, or command interpreters."));
        }

        if (WorkspaceCheckArgumentPolicy.IsRuntimeReservedToken(token))
        {
            errors.Add(new ConfigurationValidationError(
                pointer,
                "Workspace-check profiles cannot enable restore or override runtime-owned output, intermediate, package, result, or log paths."));
        }
    }

    private static void ValidateWorkspaceCheckExecutable(
        WorkspaceCheckExecutableSettings? executable,
        string? workspace,
        List<ConfigurationValidationError> errors)
    {
        string configuredPath = executable?.Path?.Trim() ?? string.Empty;
        const string pointer = "integrations.workspaceChecks.executableCatalog.dotNet.path";

        if (configuredPath.Length == 0)
        {
            return;
        }

        WorkspaceCheckExecutableConfigurationResult result =
            WorkspaceCheckExecutableConfigurationPolicy.ForCurrentPlatform()
                .Validate(configuredPath, workspace);

        if (!result.IsValid)
        {
            errors.Add(new ConfigurationValidationError(
                pointer,
                result.Error ?? "The configured workspace-check executable failed validation."));
        }
    }

    private static bool IsValidProfileId(string value)
    {
        if (value.Length is < 1 or > 64
            || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character != '-')
            {
                return false;
            }

            if (character is >= 'A' and <= 'Z')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidWorkspaceCheckTarget(
        string value,
        int maxChars)
    {
        if (value.Length > maxChars
            || value.Any(char.IsControl))
        {
            return false;
        }

        string normalized = value.Replace('\\', '/');

        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || (normalized.Length >= 2
                && char.IsAsciiLetter(normalized[0])
                && normalized[1] == ':'))
        {
            return false;
        }

        string[] segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0
            || segments.Any(static segment =>
                segment is "." or ".."
                || segment.Contains(':', StringComparison.Ordinal)))
        {
            return false;
        }

        string extension = Path.GetExtension(normalized);

        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProfileSubcommandMatchesKind(
        WorkspaceCheckKind kind,
        IReadOnlyList<string> arguments) =>
        kind switch
        {
            WorkspaceCheckKind.Build =>
                string.Equals(arguments[0], "build", StringComparison.Ordinal),
            WorkspaceCheckKind.Test =>
                string.Equals(arguments[0], "test", StringComparison.Ordinal),
            WorkspaceCheckKind.Lint =>
                string.Equals(arguments[0], "format", StringComparison.Ordinal)
                && arguments.Contains("--verify-no-changes", StringComparer.Ordinal),
            _ => false,
        };

    private static bool ParserMatchesKind(
        WorkspaceCheckKind kind,
        WorkspaceCheckDiagnosticParserKind parser) =>
        (kind, parser) switch
        {
            (WorkspaceCheckKind.Build, WorkspaceCheckDiagnosticParserKind.MsBuild) => true,
            (WorkspaceCheckKind.Test, WorkspaceCheckDiagnosticParserKind.VsTest) => true,
            (WorkspaceCheckKind.Lint, WorkspaceCheckDiagnosticParserKind.DotNetFormat) => true,
            _ => false,
        };

    private static bool IsScriptPath(string value) =>
        Path.GetExtension(value).ToLowerInvariant() is
            ".sh" or ".bash" or ".zsh"
            or ".cmd" or ".bat" or ".ps1"
            or ".py" or ".pyw" or ".js" or ".mjs"
            or ".rb" or ".pl" or ".fsx" or ".csx";

    private static void ValidatePricing(
        PricingSettings pricing,
        List<ConfigurationValidationError> errors)
    {
        ValidatePricingEntry(pricing.DefaultPricing, "cost.pricing.defaultPricing", errors);

        foreach ((string model, ModelPricingEntry entry) in pricing.ModelPricing)
        {
            ValidatePricingEntry(entry, $"cost.pricing.modelPricing[{model}]", errors);
        }
    }

    private static void ValidatePricingEntry(
        ModelPricingEntry entry,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        ValidatePricingRate(entry.InputPer1M, $"{pointer}.inputPer1M", errors);
        ValidatePricingRate(entry.OutputPer1M, $"{pointer}.outputPer1M", errors);
        ValidatePricingRate(entry.CachedPer1M, $"{pointer}.cachedPer1M", errors);

        if (entry.ReasoningPer1M is decimal reasoning)
        {
            ValidatePricingRate(reasoning, $"{pointer}.reasoningPer1M", errors);
        }
    }

    private static void ValidatePricingRate(
        decimal value,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        if (value != ArcanumSettingClamps.PricingRatePer1M(value))
        {
            errors.Add(new ConfigurationValidationError(
                pointer,
                $"Pricing rate ({value}) must be between 0 and 1,000,000 USD per 1M tokens."));
        }
    }

    private void ValidateEventBus(ArcanumSettings settings)
    {

        EventBusSettings eventBus = settings.ResolveEventBus();

        int maxConnections = ArcanumSettingClamps.MaxSseConnections(eventBus.MaxSseConnections);

        int perTypeLimit = ArcanumSettingClamps.SseConnectionsPerType(eventBus.MaxSseConnectionsPerType);

        if (maxConnections > 0 && perTypeLimit > maxConnections)
        {

            // Not a failure — the global cap triggers first and remains safe, but the per-type
            // cap can never engage, which likely does not match operator intent.
            logger?.LogWarning(
                "Arcanum:Execution:MaxSseConnectionsPerType ({PerTypeLimit}) exceeds Arcanum:Execution:MaxSseConnections ({MaxConnections}); the global cap will always trigger first, making the per-type cap meaningless.",
                perTypeLimit,
                maxConnections);

        }

    }

    /// <summary>
    /// RAG Phase 1 — The Weave &amp; Divination. When the embeddings substrate is enabled directly
    /// or derived from an embedding-backed feature, the integration provider must reference an
    /// existing provider name and its model must be non-empty.
    /// </summary>
    private static void ValidateEmbeddings(ArcanumSettings settings, List<ConfigurationValidationError> errors)
    {

        EmbeddingSettings embeddings = settings.ResolveEmbeddings();

        if (embeddings.Enabled)
        {

            if (string.IsNullOrWhiteSpace(embeddings.Provider))
            {

                errors.Add(new ConfigurationValidationError(
                    "integrations.embeddings.provider",
                    "Arcanum:Integrations:Embeddings:Provider is required when an embedding-backed feature is enabled."));

            }
            else if (!ProviderResolver.TryResolveProviderByName(settings, embeddings.Provider, out ProviderSettings? embeddingProvider)
                || embeddingProvider is null)
            {

                errors.Add(new ConfigurationValidationError(
                    "integrations.embeddings.provider",
                    $"Arcanum:Integrations:Embeddings:Provider '{embeddings.Provider}' does not match any configured provider."));

            }
            else if (embeddingProvider.Type != AiProviderKind.OpenAICompatible)
            {

                errors.Add(new ConfigurationValidationError(
                    "integrations.embeddings.provider",
                    $"Arcanum:Integrations:Embeddings:Provider '{embeddings.Provider}' must be type OpenAICompatible (Ollama embeddings via /v1 with exact model names). A Familiar serves chat completions only and has no embedding surface."));

            }

            if (string.IsNullOrWhiteSpace(embeddings.Model))
            {

                errors.Add(new ConfigurationValidationError(
                    "integrations.embeddings.model",
                    "Arcanum:Integrations:Embeddings:Model is required when an embedding-backed feature is enabled."));

            }

        }

    }

    /// <summary>
    /// Scrying — vision/multimodality. Validates that the retained MIME policy is non-empty when
    /// the feature is enabled —
    /// an empty allow-list would silently reject every image even though the operator turned
    /// Scrying on.
    /// </summary>
    private static void ValidateScrying(ArcanumSettings settings, List<ConfigurationValidationError> errors)
    {

        ScryingSettings scrying = settings.ResolveScrying();

        if (scrying.Enabled && (scrying.AllowedMimeTypes is null || scrying.AllowedMimeTypes.Length == 0))
        {

            errors.Add(new ConfigurationValidationError(
                "security.allowedImageMimeTypes",
                "Security.AllowedImageMimeTypes must not be empty when Features.Scrying is true."));

        }

    }

    private static void ValidateReasoningFacts(
        ModelReasoningSettings? reasoning,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        if (reasoning is null || (reasoning.WireDialect is null && reasoning.MaxBudgetTokens is null))
        {
            return;
        }

        bool validWireDialect = reasoning.WireDialect is null || Enum.IsDefined(reasoning.WireDialect.Value);

        if (!validWireDialect)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.wireDialect",
                $"Reasoning WireDialect '{reasoning.WireDialect}' is not defined."));
        }

        if (reasoning.MaxBudgetTokens is { } maxBudgetTokens)
        {
            if (maxBudgetTokens != ArcanumSettingClamps.ReasoningBudgetTokens(maxBudgetTokens))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.maxBudgetTokens",
                    $"Reasoning MaxBudgetTokens ({maxBudgetTokens}) must be within the 1-2,097,152 token clamp range."));
            }
        }

        // The adapter enforces this at request time; validating it at config load gives a clearer
        // operator-facing error and rejects a silently unusable combination.
        if (validWireDialect
            && reasoning.MaxBudgetTokens is not null
            && reasoning.WireDialect == ReasoningWireDialect.Standard)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.wireDialect",
                "A numeric reasoning budget requires an explicitly configured nonstandard wire dialect."));
        }
    }

    /// <summary>
    /// A Familiar carries no endpoint and no credential reference — there is nothing for Arcanum to
    /// hold, because the CLI authenticates itself against the operator's own subscription. Both are
    /// rejected rather than ignored so a row copied from an HTTP provider fails loudly at startup
    /// instead of quietly implying Arcanum is using a key it never reads.
    /// </summary>
    private static void ValidateFamiliarProvider(
        ProviderSettings provider,
        string providerPointer,
        List<ConfigurationValidationError> errors)
    {

        string kind = provider.Type.ToString();

        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
        {

            errors.Add(new ConfigurationValidationError(
                $"{providerPointer}.endpoint",
                $"Provider '{provider.Name}' is a {kind} provider, which is invoked as a local command and has no endpoint. Remove endpoint."));

        }

        if (!string.IsNullOrWhiteSpace(provider.CredentialEnvironmentVariable))
        {

            errors.Add(new ConfigurationValidationError(
                $"{providerPointer}.credentialEnvironmentVariable",
                $"Provider '{provider.Name}' is a {kind} provider, which signs in through its own CLI. Arcanum never reads its credentials, so remove credentialEnvironmentVariable and run `{FamiliarProviders.SignInCommand(provider.Type)}` instead."));

        }

        if (provider.Command is not null && string.IsNullOrWhiteSpace(provider.Command))
        {

            errors.Add(new ConfigurationValidationError(
                $"{providerPointer}.command",
                $"Provider '{provider.Name}' command must name the {FamiliarProviders.DisplayName(provider.Type)} binary, or be omitted to resolve `{FamiliarProviders.DefaultCommand(provider.Type)}` on PATH."));

        }

        ValidateHiddenModels(provider, providerPointer, errors);

    }

    /// <summary>
    /// <c>command</c> and <c>hiddenModels</c> describe a Familiar and mean nothing to an HTTP
    /// provider — an HTTP row hides a model by deleting its <c>models</c> entry. Accepting them
    /// silently would let an operator believe a hide list was in force where none can apply.
    /// </summary>
    private static void ValidateHttpProviderOnlyFields(
        ProviderSettings provider,
        string providerPointer,
        List<ConfigurationValidationError> errors)
    {

        if (provider.Command is not null)
        {

            errors.Add(new ConfigurationValidationError(
                $"{providerPointer}.command",
                $"Provider '{provider.Name}' is an OpenAICompatible provider, which is reached over HTTP rather than spawned. Remove command."));

        }

        if (provider.HiddenModels is { Length: > 0 })
        {

            errors.Add(new ConfigurationValidationError(
                $"{providerPointer}.hiddenModels",
                $"Provider '{provider.Name}' is an OpenAICompatible provider, whose models array is already the operator's own list. Remove the models entry instead of hiding it."));

        }

    }

    private static void ValidateHiddenModels(
        ProviderSettings provider,
        string providerPointer,
        List<ConfigurationValidationError> errors)
    {

        string[] hidden = provider.HiddenModels ?? [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < hidden.Length; i++)
        {

            string pointer = $"{providerPointer}.hiddenModels[{i}]";

            string entry = hidden[i] ?? string.Empty;

            if (string.IsNullOrWhiteSpace(entry))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Provider '{provider.Name}' hiddenModels entries must name a model; blank entries hide nothing."));

                continue;

            }

            if (!seen.Add(entry.Trim()))
            {

                errors.Add(new ConfigurationValidationError(
                    pointer,
                    $"Provider '{provider.Name}' hides '{entry}' more than once; matching is case-insensitive."));

            }

        }

    }

    private static void ValidateOptionalEnvironmentVariableName(
        string? name,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string trimmed = name.Trim();
        if (EnvironmentCredentialResolver.IsValidEnvironmentVariableName(trimmed))
        {
            if (trimmed.StartsWith("Arcanum__", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("ARCANUM_Arcanum__", StringComparison.OrdinalIgnoreCase)
                || ReservedRuntimeEnvironmentVariableNames.Contains(trimmed))
            {
                errors.Add(new ConfigurationValidationError(
                    pointer,
                    "Secret environment-variable references must not overlap Arcanum's configuration or explicit runtime-override namespace."));
            }

            return;
        }

        errors.Add(new ConfigurationValidationError(
            pointer,
            "Environment-variable references must use portable names: an ASCII letter or '_' followed by ASCII letters, digits, or '_' only."));
    }

    /// <summary>
    /// Validates the A2A protocol facts an operator can set. These are shapes, not policy: a bad remote
    /// allowlist entry or a header name the HTTP stack will refuse should surface as a startup error rather
    /// than as a runtime failure on the first Sending.
    /// </summary>
    private static void ValidateA2A(
        ConclaveA2ASettings settings,
        List<ConfigurationValidationError> errors)
    {

        if (!string.IsNullOrWhiteSpace(settings.ServerPath)
            && settings.ServerPath.Contains("://", StringComparison.Ordinal))
        {

            errors.Add(new ConfigurationValidationError(
                "integrations.a2A.serverPath",
                "A2A ServerPath must be a path, not an absolute URL. A path outside /api is mounted under it."));

        }

        string[] allowedRemoteAgents = settings.AllowedRemoteAgents ?? [];

        for (int index = 0; index < allowedRemoteAgents.Length; index++)
        {

            string entry = allowedRemoteAgents[index];

            if (string.IsNullOrWhiteSpace(entry))
            {

                continue;

            }

            if (!Uri.TryCreate(entry.Trim(), UriKind.Absolute, out Uri? allowed)
                || (allowed.Scheme != Uri.UriSchemeHttp && allowed.Scheme != Uri.UriSchemeHttps))
            {

                errors.Add(new ConfigurationValidationError(
                    $"integrations.a2A.allowedRemoteAgents[{index}]",
                    $"'{entry}' must be an absolute http or https URL or origin; it can never match a remote agent as written."));

            }

        }

        if (!string.IsNullOrWhiteSpace(settings.OutboundCredentialHeader)
            && settings.OutboundCredentialHeader.AsSpan().ContainsAny(InvalidHeaderNameCharacters))
        {

            errors.Add(new ConfigurationValidationError(
                "integrations.a2A.outboundCredentialHeader",
                "The outbound credential header name must be a single HTTP token (no whitespace, colons, or separators)."));

        }

        // A typo here is silent at runtime — callback mode simply falls back to waiting inline — so it is
        // caught at startup rather than left for an operator to notice as "why is nothing ever
        // asynchronous" (issue #67).
        if (!string.IsNullOrWhiteSpace(settings.PushCallbackBaseUrl)
            && (!Uri.TryCreate(settings.PushCallbackBaseUrl.Trim(), UriKind.Absolute, out Uri? callbackBase)
                || (callbackBase.Scheme != Uri.UriSchemeHttp && callbackBase.Scheme != Uri.UriSchemeHttps)))
        {

            errors.Add(new ConfigurationValidationError(
                "integrations.a2A.pushCallbackBaseUrl",
                "The push callback base URL must be an absolute http or https URL a remote agent can reach, "
                + "for example https://arcanum.example.com."));

        }

        ValidateModes(settings.InputModes, "integrations.a2A.inputModes", errors);

        ValidateModes(settings.OutputModes, "integrations.a2A.outputModes", errors);

        A2ASkillSettings[] skills = settings.Skills ?? [];

        HashSet<string> seenSkillIds = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < skills.Length; index++)
        {

            A2ASkillSettings skill = skills[index];

            if (string.IsNullOrWhiteSpace(skill.Id))
            {

                // A skill with no id is silently dropped from the card, so say so at startup rather than
                // letting an operator wonder why their declaration never appeared (issue #63).
                errors.Add(new ConfigurationValidationError(
                    $"integrations.a2A.skills[{index}].id",
                    "An advertised A2A skill requires a non-empty id; peers match on it."));

            }
            else if (!seenSkillIds.Add(skill.Id.Trim()))
            {

                errors.Add(new ConfigurationValidationError(
                    $"integrations.a2A.skills[{index}].id",
                    $"Advertised A2A skill id '{skill.Id.Trim()}' is declared more than once."));

            }

            ValidateModes(skill.InputModes, $"integrations.a2A.skills[{index}].inputModes", errors);

            ValidateModes(skill.OutputModes, $"integrations.a2A.skills[{index}].outputModes", errors);

        }

    }

    /// <summary>
    /// Advertised modalities are media types. A malformed entry is only ever visible on someone else's
    /// Agent Card reader, so it has to fail at startup instead.
    /// </summary>
    private static void ValidateModes(
        string[]? modes,
        string path,
        List<ConfigurationValidationError> errors)
    {

        foreach (string mode in modes ?? [])
        {

            if (string.IsNullOrWhiteSpace(mode))
            {

                continue;

            }

            string trimmed = mode.Trim();

            int slash = trimmed.IndexOf('/', StringComparison.Ordinal);

            if (slash <= 0
                || slash == trimmed.Length - 1
                || trimmed.AsSpan().ContainsAny(WhitespaceCharacters))
            {

                errors.Add(new ConfigurationValidationError(
                    path,
                    $"'{mode}' is not a media type; advertised A2A modalities look like 'text/plain'."));

            }

        }

    }

    private static readonly System.Buffers.SearchValues<char> InvalidHeaderNameCharacters =
        System.Buffers.SearchValues.Create(" \t:()<>@,;\\\"/[]?={}");

    private static readonly System.Buffers.SearchValues<char> WhitespaceCharacters =
        System.Buffers.SearchValues.Create(" \t\r\n");

    private static void ValidateWebResearch(
        WebResearchIntegrationSettings settings,
        List<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(settings.SearchProvider))
        {
            errors.Add(new ConfigurationValidationError(
                "integrations.webResearch.searchProvider",
                "Web-research SearchProvider must not be blank."));
        }

        if (!WebResearchModels.IsSupportedPerplexityModel(settings.PerplexityModel))
        {
            errors.Add(new ConfigurationValidationError(
                "integrations.webResearch.perplexityModel",
                "PerplexityModel must be 'sonar' or 'sonar-pro'."));
        }
    }

    private static void AddEnvironmentVariableReference(
        string name,
        string pointer,
        List<EnvironmentVariableReference> references)
    {
        if (EnvironmentCredentialResolver.IsValidEnvironmentVariableName(name))
        {
            references.Add(new EnvironmentVariableReference(name, pointer));
        }
    }

    private static void ValidateUniqueEnvironmentVariableReferences(
        IReadOnlyList<EnvironmentVariableReference> references,
        List<ConfigurationValidationError> errors)
    {
        foreach (IGrouping<string, EnvironmentVariableReference> collision in references
                     .GroupBy(
                         static reference => reference.Name,
                         StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            foreach (EnvironmentVariableReference reference in collision)
            {
                AddErrorIfMissing(
                    new ConfigurationValidationError(
                        reference.Pointer,
                        "Secret environment-variable references must resolve to unique names case-insensitively; this reference collides with another configured secret reference."),
                    errors);
            }
        }
    }

    private readonly record struct EnvironmentVariableReference(
        string Name,
        string Pointer);

    /// <summary>
    /// Optional HTTPS binding. When <see cref="HttpsSettings.Enabled"/> is <c>true</c>, the certificate
    /// path must be set, the TLS port must be within range and distinct from the plaintext HTTP port, and
    /// the referenced file(s) must exist on disk. All-interfaces bind (<see cref="HostSettings.ListenAny"/>
    /// / <c>ARCANUM_HOST_ANY</c>) additionally requires HTTPS enabled — plaintext any-IP HTTP is refused.
    /// No PKCS#12/PEM cryptographic load happens here — that is deferred to the Infrastructure loader at
    /// bind time — and the certificate password environment value is never read or echoed into any
    /// validation error.
    /// </summary>
    private static void ValidateHttps(HostSettings host, List<ConfigurationValidationError> errors)
    {

        HttpsSettings https = host.Https ?? new HttpsSettings();

        ValidateOptionalEnvironmentVariableName(
            https.CertificatePasswordEnvironmentVariable,
            "host.https.certificatePasswordEnvironmentVariable",
            errors);

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

    /// <summary>
    /// Whether a configured provider can serve this model name at startup.
    /// </summary>
    /// <remarks>
    /// A Familiar answers yes to any name. Its catalogue belongs to the vendor, so the whole point of
    /// the kind is that a newly released model works without an <c>arcanum.json</c> edit — validating
    /// <c>defaultModel</c> against a declared list would reject exactly the configuration the feature
    /// exists to support, and would contradict <c>ProviderResolver</c>, which resolves the same name
    /// by pass-through. The CLI is the authority on whether the model exists, and it says so at turn
    /// time with an actionable message.
    /// </remarks>
    private static bool ModelExists(ArcanumSettings settings, string model)
    {

        ProviderSettings[] providers = settings.Providers ?? [];

        for (int i = 0; i < providers.Length; i++)
        {

            if (FamiliarProviders.IsFamiliar(providers[i]))
            {

                return true;

            }

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
