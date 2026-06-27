using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ConfigurationValidator
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

        if (errors.Count > 0)
        {

            return Result.Failure(new Error(
                "Configuration.ValidationFailed",
                $"{errors.Count} configuration validation error(s).",
                errors));

        }

        return Result.Success();

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
