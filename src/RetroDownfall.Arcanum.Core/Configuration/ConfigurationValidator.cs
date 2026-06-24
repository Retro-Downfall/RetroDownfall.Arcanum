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

            if (provider.Type == AiProviderKind.LlamaCppServer)
            {

                bool hasModels = provider.Models.Length > 0;

                bool hasMap = provider.LlamaCpp?.ModelMap is { Count: > 0 };

                if (!hasModels && !hasMap)
                {

                    errors.Add(new ConfigurationValidationError(
                        providerPointer,
                        $"Provider '{provider.Name}' has no configured models or llamaCpp.modelMap entries."));

                }

            }
            else if (provider.Models.Length == 0)
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

        long toolOutputCapBytes = ArcanumSettingClamps.ToolOutputCapBytes(settings.Intelligence.ToolOutputCapBytes);

        int maxJsonRpcLineBytes = ArcanumSettingClamps.McpMaxJsonRpcLineBytes(settings.Mcp.MaxJsonRpcLineBytes);

        long effectiveToolOutputCapBytes = ArcanumSettingClamps.EffectiveInProcessToolOutputCapBytes(
            settings.Intelligence.ToolOutputCapBytes,
            maxJsonRpcLineBytes);

        if (effectiveToolOutputCapBytes < toolOutputCapBytes)
        {

            errors.Add(new ConfigurationValidationError(
                "mcp.maxJsonRpcLineBytes",
                $"Mcp.MaxJsonRpcLineBytes ({maxJsonRpcLineBytes}) is too small for Intelligence.ToolOutputCapBytes ({toolOutputCapBytes}) after JSON-RPC envelope and escaping margin."));

        }

        int executeCommandTimeoutSeconds = ArcanumSettingClamps.ExecuteCommandTimeoutSeconds(
            settings.Intelligence.ExecuteCommandTimeoutSeconds);

        int mcpRequestTimeoutSeconds = ArcanumSettingClamps.McpRequestTimeoutSeconds(
            settings.Mcp.RequestTimeoutSeconds);

        if (mcpRequestTimeoutSeconds < executeCommandTimeoutSeconds)
        {

            errors.Add(new ConfigurationValidationError(
                "mcp.requestTimeoutSeconds",
                $"Mcp.RequestTimeoutSeconds ({mcpRequestTimeoutSeconds}) must be at least Intelligence.ExecuteCommandTimeoutSeconds ({executeCommandTimeoutSeconds}) so in-process execute_command calls are not orphaned by the MCP JSON-RPC deadline."));

        }

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

}
