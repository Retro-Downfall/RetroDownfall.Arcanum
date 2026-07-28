using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ConfigurationValidator(
    ILogger<ConfigurationValidator>? logger = null,
    IWorkspaceCheckAdvertisementEligibility? workspaceCheckEligibility = null)
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

    internal const string ObsoleteModerationsMigrationMessage =
        "Arcanum:Moderations is no longer supported. POST /v1/moderations always returns 501 not_supported; remove the Moderations block from arcanum.json.";

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

        if (arcanum.GetSection("Moderations").Exists())
        {
            errors.Add(new ConfigurationValidationError(
                "moderations",
                ObsoleteModerationsMigrationMessage));
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

            ValidatePromptCachingEnumStrings(
                provider.GetSection("PromptCaching"),
                $"{pointer}.promptCaching",
                errors);

            foreach (IConfigurationSection model in provider.GetSection("Models").GetChildren())
            {
                string modelPointer = string.IsNullOrEmpty(model.Key)
                    ? $"{pointer}.models"
                    : $"{pointer}.models[{model.Key}]";

                ValidatePromptCachingEnumStrings(
                    model.GetSection("PromptCaching"),
                    $"{modelPointer}.promptCaching",
                    errors);
            }
        }

        if (errors.Count > 0)
        {
            return Result.Failure(new Error(
                "Configuration.ValidationFailed",
                $"{errors.Count} configuration issue(s).",
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

        if (TryGetPropertyIgnoreCase(root, "moderations", out _))
        {
            errors.Add(new ConfigurationValidationError(
                "moderations",
                ObsoleteModerationsMigrationMessage));
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

    private static void ValidatePromptCachingEnumStrings(
        IConfigurationSection profile,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        foreach (string property in new[] { "ControlMode", "WireDialect", "Retention" })
        {
            string? value = profile[property];

            if (value is not null && int.TryParse(value, out _))
            {
                string field = char.ToLowerInvariant(property[0]) + property[1..];

                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.{field}",
                    $"Prompt caching {property} must use a named string value, not a number."));
            }
        }
    }

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

            ValidateTokenizationProfile(
                provider.Tokenization,
                $"{providerPointer}.tokenization",
                errors);
            ValidatePromptCachingProfile(
                provider.PromptCaching,
                $"{providerPointer}.promptCaching",
                provider.SupportsPromptCaching,
                errors);

            if (models.Count == 0)
            {

                errors.Add(new ConfigurationValidationError(
                    providerPointer,
                    $"Provider '{provider.Name}' has no configured models."));

            }

            for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
            {
                ValidateReasoningCapabilities(
                    models[modelIndex].Reasoning,
                    $"{providerPointer}.models[{modelIndex}].reasoning",
                    errors);
                ValidateTokenizationProfile(
                    models[modelIndex].Tokenization,
                    $"{providerPointer}.models[{modelIndex}].tokenization",
                    errors);
                ValidatePromptCachingProfile(
                    models[modelIndex].PromptCaching,
                    $"{providerPointer}.models[{modelIndex}].promptCaching",
                    provider.SupportsPromptCaching,
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

        ValidatePricing(settings.Pricing ?? new PricingSettings(), errors);

        IntelligenceSettings intelligence = settings.Intelligence ?? new IntelligenceSettings();

        if (intelligence.EstimatedTokenSafetyMarginPercent
            != ArcanumSettingClamps.EstimatedTokenSafetyMarginPercent(
                intelligence.EstimatedTokenSafetyMarginPercent))
        {
            errors.Add(new ConfigurationValidationError(
                "intelligence.estimatedTokenSafetyMarginPercent",
                $"Intelligence.EstimatedTokenSafetyMarginPercent ({intelligence.EstimatedTokenSafetyMarginPercent}) must be within the 1-100 clamp range."));
        }

        if (intelligence.UnknownImageTokenReserve
            != ArcanumSettingClamps.UnknownImageTokenReserve(
                intelligence.UnknownImageTokenReserve))
        {
            errors.Add(new ConfigurationValidationError(
                "intelligence.unknownImageTokenReserve",
                $"Intelligence.UnknownImageTokenReserve ({intelligence.UnknownImageTokenReserve}) must be within the 1-128,000 clamp range."));
        }

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

        ValidateCodingTools(
            settings,
            errors,
            workspaceCheckEligibility?.IsCurrentlyEligible == true);

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

    private static void ValidateCodingTools(
        ArcanumSettings settings,
        List<ConfigurationValidationError> errors,
        bool workspaceCheckEligibleForAdvertisement)
    {
        CodingToolsSettings codingTools = settings.CodingTools ?? new CodingToolsSettings();
        WorkspaceSearchSettings search = codingTools.Search ?? new WorkspaceSearchSettings();
        WorkspacePatchSettings patch = codingTools.Patch ?? new WorkspacePatchSettings();
        WorkspaceCheckSettings check = codingTools.WorkspaceCheck ?? new WorkspaceCheckSettings();

        ValidateBound(
            search.MaxPatternChars,
            ArcanumSettingClamps.WorkspaceSearchMaxPatternChars(search.MaxPatternChars),
            "codingTools.search.maxPatternChars",
            "1-16,384",
            errors);
        ValidateBound(
            search.RegexTimeoutMilliseconds,
            ArcanumSettingClamps.WorkspaceSearchRegexTimeoutMilliseconds(search.RegexTimeoutMilliseconds),
            "codingTools.search.regexTimeoutMilliseconds",
            "10-10,000",
            errors);
        ValidateBound(
            search.MaxElapsedMilliseconds,
            ArcanumSettingClamps.WorkspaceSearchMaxElapsedMilliseconds(search.MaxElapsedMilliseconds),
            "codingTools.search.maxElapsedMilliseconds",
            "100-120,000",
            errors);
        ValidateBound(
            search.MaxFiles,
            ArcanumSettingClamps.WorkspaceSearchMaxFiles(search.MaxFiles),
            "codingTools.search.maxFiles",
            "1-100,000",
            errors);
        ValidateBound(
            search.MaxBytes,
            ArcanumSettingClamps.WorkspaceSearchMaxBytes(search.MaxBytes),
            "codingTools.search.maxBytes",
            "1,024-1,073,741,824",
            errors);
        ValidateBound(
            search.MaxTraversalSteps,
            ArcanumSettingClamps.WorkspaceSearchMaxTraversalSteps(search.MaxTraversalSteps),
            "codingTools.search.maxTraversalSteps",
            "1-10,000,000",
            errors);
        ValidateBound(
            search.MaxMatches,
            ArcanumSettingClamps.WorkspaceSearchMaxMatches(search.MaxMatches),
            "codingTools.search.maxMatches",
            "1-100,000",
            errors);
        ValidateBound(
            search.MaxPreviewChars,
            ArcanumSettingClamps.WorkspaceSearchMaxPreviewChars(search.MaxPreviewChars),
            "codingTools.search.maxPreviewChars",
            "16-4,096",
            errors);

        ValidateBound(
            patch.MaxPatchBytes,
            ArcanumSettingClamps.WorkspacePatchMaxPatchBytes(patch.MaxPatchBytes),
            "codingTools.patch.maxPatchBytes",
            "1,024-67,108,864",
            errors);
        ValidateBound(
            patch.MaxInputBytesPerFile,
            ArcanumSettingClamps.WorkspacePatchMaxInputBytesPerFile(
                patch.MaxInputBytesPerFile),
            "codingTools.patch.maxInputBytesPerFile",
            "1,024-268,435,456",
            errors);
        ValidateBound(
            patch.MaxTotalInputBytes,
            ArcanumSettingClamps.WorkspacePatchMaxTotalInputBytes(
                patch.MaxTotalInputBytes),
            "codingTools.patch.maxTotalInputBytes",
            "1,024-1,073,741,824",
            errors);
        ValidateBound(
            patch.MaxOutputBytesPerFile,
            ArcanumSettingClamps.WorkspacePatchMaxOutputBytesPerFile(
                patch.MaxOutputBytesPerFile),
            "codingTools.patch.maxOutputBytesPerFile",
            "1,024-268,435,456",
            errors);
        ValidateBound(
            patch.MaxTotalOutputBytes,
            ArcanumSettingClamps.WorkspacePatchMaxTotalOutputBytes(
                patch.MaxTotalOutputBytes),
            "codingTools.patch.maxTotalOutputBytes",
            "1,024-1,073,741,824",
            errors);
        ValidateBound(
            patch.MaxStagingBytesPerFile,
            ArcanumSettingClamps.WorkspacePatchMaxStagingBytesPerFile(
                patch.MaxStagingBytesPerFile),
            "codingTools.patch.maxStagingBytesPerFile",
            "1,024-536,870,912",
            errors);
        ValidateBound(
            patch.MaxTotalStagingBytes,
            ArcanumSettingClamps.WorkspacePatchMaxTotalStagingBytes(
                patch.MaxTotalStagingBytes),
            "codingTools.patch.maxTotalStagingBytes",
            "1,024-2,147,483,648",
            errors);
        ValidateBound(
            patch.MaxElapsedMilliseconds,
            ArcanumSettingClamps.WorkspacePatchMaxElapsedMilliseconds(
                patch.MaxElapsedMilliseconds),
            "codingTools.patch.maxElapsedMilliseconds",
            "100-300,000",
            errors);
        ValidateBound(
            patch.RollbackReserveMilliseconds,
            ArcanumSettingClamps.WorkspacePatchRollbackReserveMilliseconds(
                patch.RollbackReserveMilliseconds),
            "codingTools.patch.rollbackReserveMilliseconds",
            "50-60,000",
            errors);
        if (patch.RollbackReserveMilliseconds >= patch.MaxElapsedMilliseconds)
        {

            errors.Add(
                new ConfigurationValidationError(
                    "codingTools.patch.rollbackReserveMilliseconds",
                    $"CodingTools.Patch.RollbackReserveMilliseconds ({patch.RollbackReserveMilliseconds}) must be less than MaxElapsedMilliseconds ({patch.MaxElapsedMilliseconds})."));

        }
        ValidateBound(
            patch.MaxFiles,
            ArcanumSettingClamps.WorkspacePatchMaxFiles(patch.MaxFiles),
            "codingTools.patch.maxFiles",
            "1-1,000",
            errors);
        ValidateBound(
            patch.MaxHunks,
            ArcanumSettingClamps.WorkspacePatchMaxHunks(patch.MaxHunks),
            "codingTools.patch.maxHunks",
            "1-10,000",
            errors);
        ValidateBound(
            patch.MaxLinesPerHunk,
            ArcanumSettingClamps.WorkspacePatchMaxLinesPerHunk(patch.MaxLinesPerHunk),
            "codingTools.patch.maxLinesPerHunk",
            "1-100,000",
            errors);
        ValidateBound(
            patch.FuzzyMatchWindowLines,
            ArcanumSettingClamps.WorkspacePatchFuzzyMatchWindowLines(patch.FuzzyMatchWindowLines),
            "codingTools.patch.fuzzyMatchWindowLines",
            "0-1,000",
            errors);
        ValidateBound(
            patch.MaxResultItems,
            ArcanumSettingClamps.WorkspacePatchMaxResultItems(patch.MaxResultItems),
            "codingTools.patch.maxResultItems",
            "1-10,000",
            errors);

        ValidateBound(
            check.TimeoutSeconds,
            ArcanumSettingClamps.WorkspaceCheckTimeoutSeconds(check.TimeoutSeconds),
            "codingTools.workspaceCheck.timeoutSeconds",
            "30-1,800",
            errors);
        ValidateBound(
            check.MaxCustomProfiles,
            ArcanumSettingClamps.WorkspaceCheckMaxCustomProfiles(check.MaxCustomProfiles),
            "codingTools.workspaceCheck.maxCustomProfiles",
            "0-256",
            errors);
        ValidateBound(
            check.MaxFixedArgumentsPerProfile,
            ArcanumSettingClamps.WorkspaceCheckMaxFixedArgumentsPerProfile(
                check.MaxFixedArgumentsPerProfile),
            "codingTools.workspaceCheck.maxFixedArgumentsPerProfile",
            "1-128",
            errors);
        ValidateBound(
            check.MaxArgumentTokenChars,
            ArcanumSettingClamps.WorkspaceCheckMaxArgumentTokenChars(check.MaxArgumentTokenChars),
            "codingTools.workspaceCheck.maxArgumentTokenChars",
            "16-4,096",
            errors);
        ValidateBound(
            check.MaxOptionsPerProfile,
            ArcanumSettingClamps.WorkspaceCheckMaxOptionsPerProfile(check.MaxOptionsPerProfile),
            "codingTools.workspaceCheck.maxOptionsPerProfile",
            "0-64",
            errors);
        ValidateBound(
            check.MaxAllowedValuesPerOption,
            ArcanumSettingClamps.WorkspaceCheckMaxAllowedValuesPerOption(
                check.MaxAllowedValuesPerOption),
            "codingTools.workspaceCheck.maxAllowedValuesPerOption",
            "1-128",
            errors);
        ValidateBound(
            check.MaxDiagnostics,
            ArcanumSettingClamps.WorkspaceCheckMaxDiagnostics(check.MaxDiagnostics),
            "codingTools.workspaceCheck.maxDiagnostics",
            "1-10,000",
            errors);
        ValidateBound(
            check.MaxOutputBytes,
            ArcanumSettingClamps.WorkspaceCheckMaxOutputBytes(check.MaxOutputBytes),
            "codingTools.workspaceCheck.maxOutputBytes",
            "4,096-67,108,864",
            errors);

        ValidateWorkspaceCheckExecutable(
            check.ExecutableCatalog?.DotNet,
            settings.Host?.Workspace,
            errors);
        ValidateWorkspaceCheckProfiles(check, errors);

        if (check.Enabled && workspaceCheckEligibleForAdvertisement)
        {
            int checkTimeout = ArcanumSettingClamps.WorkspaceCheckTimeoutSeconds(
                check.TimeoutSeconds);
            int inferenceTimeout = ArcanumSettingClamps.InferenceTimeoutSeconds(
                (settings.Intelligence ?? new IntelligenceSettings()).InferenceTimeoutSeconds);

            if ((long)checkTimeout + ArcanumSettingClamps.WorkspaceCheckCleanupGraceSeconds
                > inferenceTimeout)
            {
                errors.Add(new ConfigurationValidationError(
                    "codingTools.workspaceCheck.timeoutSeconds",
                    $"CodingTools.WorkspaceCheck.TimeoutSeconds ({checkTimeout}) plus "
                    + $"{ArcanumSettingClamps.WorkspaceCheckCleanupGraceSeconds} seconds of cleanup grace "
                    + $"must not exceed Intelligence.InferenceTimeoutSeconds ({inferenceTimeout}) "
                    + "while workspace_check is eligible for advertisement."));
            }
        }
    }

    private static void ValidateWorkspaceCheckProfiles(
        WorkspaceCheckSettings check,
        List<ConfigurationValidationError> errors)
    {
        Dictionary<string, WorkspaceCheckProfileSettings> profiles =
            check.CustomProfiles ?? new Dictionary<string, WorkspaceCheckProfileSettings>();
        int maxProfiles = ArcanumSettingClamps.WorkspaceCheckMaxCustomProfiles(
            check.MaxCustomProfiles);

        if (profiles.Count > maxProfiles)
        {
            errors.Add(new ConfigurationValidationError(
                "codingTools.workspaceCheck.customProfiles",
                $"WorkspaceCheck.CustomProfiles contains {profiles.Count} entries; the configured cap is {maxProfiles}."));
        }

        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string profileId, WorkspaceCheckProfileSettings? profile) in profiles)
        {
            string pointer = $"codingTools.workspaceCheck.customProfiles[{profileId}]";

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
            check.MaxFixedArgumentsPerProfile);
        int maxTokenChars = ArcanumSettingClamps.WorkspaceCheckMaxArgumentTokenChars(
            check.MaxArgumentTokenChars);

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
                $"Workspace-check profile '{profileId}' has {fixedArguments.Length} fixed arguments; the configured cap is {maxFixedArguments}."));
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
            check.MaxOptionsPerProfile);

        if (options.Count > maxOptions)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.options",
                $"Workspace-check profile '{profileId}' has {options.Count} options; the configured cap is {maxOptions}."));
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
            check.MaxAllowedValuesPerOption);
        int maxTokens = ArcanumSettingClamps.WorkspaceCheckMaxFixedArgumentsPerProfile(
            check.MaxFixedArgumentsPerProfile);
        int maxTokenChars = ArcanumSettingClamps.WorkspaceCheckMaxArgumentTokenChars(
            check.MaxArgumentTokenChars);

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
                $"Workspace-check option has {values.Count} allowed values; the configured cap is {maxValues}."));
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
        const string pointer = "codingTools.workspaceCheck.executableCatalog.dotNet.path";

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

    private static void ValidateBound(
        int configured,
        int effective,
        string pointer,
        string range,
        List<ConfigurationValidationError> errors)
    {
        if (configured != effective)
        {
            errors.Add(new ConfigurationValidationError(
                pointer,
                $"Configured value ({configured}) must be within the {range} clamp range."));
        }
    }

    private static void ValidateBound(
        long configured,
        long effective,
        string pointer,
        string range,
        List<ConfigurationValidationError> errors)
    {
        if (configured != effective)
        {
            errors.Add(new ConfigurationValidationError(
                pointer,
                $"Configured value ({configured}) must be within the {range} clamp range."));
        }
    }

    private static void ValidatePricing(
        PricingSettings pricing,
        List<ConfigurationValidationError> errors)
    {
        ValidatePricingEntry(pricing.DefaultPricing, "pricing.defaultPricing", errors);

        foreach ((string model, ModelPricingEntry entry) in pricing.ModelPricing)
        {
            ValidatePricingEntry(entry, $"pricing.modelPricing[{model}]", errors);
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

    private static void ValidateReasoningCapabilities(
        ReasoningCapabilities? reasoning,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        if (reasoning is null)
        {
            return;
        }

        bool validControlSupport = Enum.IsDefined(reasoning.ControlSupport);

        if (!validControlSupport)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.controlSupport",
                $"Reasoning ControlSupport '{reasoning.ControlSupport}' is not defined."));
        }

        bool validWireDialect = Enum.IsDefined(reasoning.WireDialect);

        if (!validWireDialect)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.wireDialect",
                $"Reasoning WireDialect '{reasoning.WireDialect}' is not defined."));
        }

        bool supportsBudget = validControlSupport
            && reasoning.ControlSupport is ReasoningControlSupport.Budget
                or ReasoningControlSupport.EffortAndBudget;

        if (validControlSupport && validWireDialect)
        {
            if (supportsBudget && reasoning.WireDialect == ReasoningWireDialect.Standard)
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.wireDialect",
                    "Reasoning budget control requires an explicitly configured nonstandard numeric-budget WireDialect."));
            }
            else if (!supportsBudget && reasoning.WireDialect != ReasoningWireDialect.Standard)
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.wireDialect",
                    "A nonstandard reasoning WireDialect requires Budget or EffortAndBudget control support."));
            }
        }

        if (reasoning.MaxBudgetTokens is { } maxBudgetTokens)
        {
            if (maxBudgetTokens != ArcanumSettingClamps.ReasoningBudgetTokens(maxBudgetTokens))
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.maxBudgetTokens",
                    $"Reasoning MaxBudgetTokens ({maxBudgetTokens}) must be within the 1-2,097,152 token clamp range."));
            }

            if (validControlSupport && !supportsBudget)
            {
                errors.Add(new ConfigurationValidationError(
                    $"{pointer}.maxBudgetTokens",
                    "Reasoning MaxBudgetTokens requires Budget or EffortAndBudget control support."));
            }
        }

        bool supportsVisibleOutput = reasoning.SupportsSummary || reasoning.SupportsFull;

        if (reasoning.AllowsClientOutput && !supportsVisibleOutput)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.allowsClientOutput",
                "Reasoning AllowsClientOutput requires SupportsSummary or SupportsFull."));
        }

        if (reasoning.SupportsStreaming && !supportsVisibleOutput)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.supportsStreaming",
                "Reasoning SupportsStreaming requires SupportsSummary or SupportsFull."));
        }
    }

    private static void ValidatePromptCachingProfile(
        PromptCachingProfile? profile,
        string pointer,
        bool? legacySupportsPromptCaching,
        List<ConfigurationValidationError> errors)
    {
        if (profile is null)
        {
            return;
        }

        bool validControlMode = Enum.IsDefined(profile.ControlMode);

        if (!validControlMode)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.controlMode",
                $"Prompt caching ControlMode '{profile.ControlMode}' is not defined."));
        }

        bool validWireDialect = Enum.IsDefined(profile.WireDialect);

        if (!validWireDialect)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.wireDialect",
                $"Prompt caching WireDialect '{profile.WireDialect}' is not defined."));
        }
        else if (profile.WireDialect == PromptCachingWireDialect.OpenAiPromptCacheBreakpoints)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.wireDialect",
                "Prompt caching WireDialect 'openAiPromptCacheBreakpoints' is reserved and is not supported by the pinned provider adapter."));
        }

        if (!Enum.IsDefined(profile.Retention))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.retention",
                $"Prompt caching Retention '{profile.Retention}' is not defined."));
        }

        if (validControlMode
            && profile.ControlMode != PromptCachingControlMode.Explicit
            && (profile.EmitCacheKey
                || profile.Retention != PromptCacheRetentionPolicy.ProviderDefault
                || profile.EmitStablePrefixBreakpoint
                || profile.ToolSchemasParticipate))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.controlMode",
                "Prompt caching directives require ControlMode 'explicit'."));
        }

        if (validControlMode
            && profile.ControlMode == PromptCachingControlMode.Explicit
            && !profile.EmitCacheKey
            && profile.Retention == PromptCacheRetentionPolicy.ProviderDefault
            && !profile.EmitStablePrefixBreakpoint)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.controlMode",
                "Prompt caching ControlMode 'explicit' requires at least one emitted directive."));
        }

        if (legacySupportsPromptCaching == false
            && validControlMode
            && profile.ControlMode == PromptCachingControlMode.Explicit)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.controlMode",
                "An explicit prompt-cache profile conflicts with SupportsPromptCaching=false."));
        }

        if (profile.EmitCacheKey && !profile.CacheKeysSupported)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.emitCacheKey",
                "EmitCacheKey requires CacheKeysSupported=true."));
        }

        if (profile.Retention != PromptCacheRetentionPolicy.ProviderDefault
            && !profile.RetentionSelectionSupported)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.retention",
                "A non-default prompt-cache retention requires RetentionSelectionSupported=true."));
        }

        if (profile.Retention == PromptCacheRetentionPolicy.ThirtyMinutes)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.retention",
                "Thirty-minute retention requires a verified explicit-breakpoint dialect, which is not supported by this build."));
        }

        if (profile.EmitStablePrefixBreakpoint && !profile.StablePrefixBreakpointsSupported)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.emitStablePrefixBreakpoint",
                "EmitStablePrefixBreakpoint requires StablePrefixBreakpointsSupported=true."));
        }

        if (profile.WireDialect == PromptCachingWireDialect.OpenAiPromptCacheRetention
            && (profile.StablePrefixBreakpointsSupported || profile.EmitStablePrefixBreakpoint))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.emitStablePrefixBreakpoint",
                "The openAiPromptCacheRetention dialect does not support explicit content breakpoints."));
        }
    }

    private static void ValidateTokenizationProfile(
        ModelTokenizationProfile? profile,
        string pointer,
        List<ConfigurationValidationError> errors)
    {
        if (profile is null)
        {
            return;
        }

        if (!Enum.IsDefined(profile.Type))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.type",
                $"Tokenization profile Type '{profile.Type}' is not defined."));
        }
        else if (profile.Type == ModelTokenizationProfileType.ProviderTokenizerApi)
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.type",
                "ProviderTokenizerApi is not available for the configured provider types in this build; use an exact local tokenizer or calibrated approximation."));
        }

        if (profile.Type == ModelTokenizationProfileType.ExactLocalTokenizer
            && string.IsNullOrWhiteSpace(profile.TokenizerId))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.tokenizerId",
                "ExactLocalTokenizer requires a non-empty TokenizerId."));
        }

        if (profile.SafetyMarginPercent is { } margin
            && margin != ArcanumSettingClamps.EstimatedTokenSafetyMarginPercent(margin))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.safetyMarginPercent",
                $"Tokenization SafetyMarginPercent ({margin}) must be within the 1-100 clamp range."));
        }

        if (profile.PerMessageOverheadTokens is { } perMessage
            && perMessage != ArcanumSettingClamps.PerMessageTemplateOverheadTokens(perMessage))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.perMessageOverheadTokens",
                $"Tokenization PerMessageOverheadTokens ({perMessage}) must be within the 0-32 clamp range."));
        }

        if (profile.PerToolOverheadTokens is { } perTool
            && perTool != ArcanumSettingClamps.TokenizationPerToolOverheadTokens(perTool))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.perToolOverheadTokens",
                $"Tokenization PerToolOverheadTokens ({perTool}) must be within the 0-128 clamp range."));
        }

        if (profile.ProviderFramingTokens is { } framing
            && framing != ArcanumSettingClamps.TokenizationProviderFramingTokens(framing))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.providerFramingTokens",
                $"Tokenization ProviderFramingTokens ({framing}) must be within the 0-1,024 clamp range."));
        }

        if (profile.StopTokenOverheadTokens is { } stop
            && stop != ArcanumSettingClamps.TokenizationStopTokenOverheadTokens(stop))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.stopTokenOverheadTokens",
                $"Tokenization StopTokenOverheadTokens ({stop}) must be within the 0-128 clamp range."));
        }

        if (profile.UnknownImageReserveTokens is { } image
            && image != ArcanumSettingClamps.UnknownImageTokenReserve(image))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.unknownImageReserveTokens",
                $"Tokenization UnknownImageReserveTokens ({image}) must be within the 1-128,000 clamp range."));
        }

        if (profile.Confidence is { } confidence
            && confidence != ArcanumSettingClamps.TokenizationConfidence(confidence))
        {
            errors.Add(new ConfigurationValidationError(
                $"{pointer}.confidence",
                $"Tokenization Confidence ({confidence}) must be finite and between 0 and 1."));
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
