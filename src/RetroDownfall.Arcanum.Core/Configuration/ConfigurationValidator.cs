using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed class ConfigurationValidator
{

    public Result Validate(ArcanumSettings settings)
    {
        var errors = new List<string>();

        ProviderSettings[] providers = settings.Providers ?? [];

        foreach (ProviderSettings provider in providers)
        {
            if (provider.Type == AiProviderKind.LlamaCppServer)
            {
                bool hasModels = provider.Models.Length > 0;

                bool hasMap = provider.LlamaCpp?.ModelMap is { Count: > 0 };

                if (!hasModels && !hasMap)
                {
                    errors.Add($"Provider '{provider.Name}' has no configured models or llamaCpp.modelMap entries.");
                }
            }
            else if (provider.Models.Length == 0)
            {
                errors.Add($"Provider '{provider.Name}' has no configured models.");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            if (!ModelExists(settings, settings.DefaultModel))
            {
                errors.Add($"DefaultModel '{settings.DefaultModel}' does not match any configured provider model.");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.FastModel))
        {
            if (!ModelExists(settings, settings.FastModel))
            {
                errors.Add($"FastModel '{settings.FastModel}' does not match any configured provider model.");
            }
        }

        if (errors.Count > 0)
        {
            return Result.Failure(new Error("Configuration.ValidationFailed", string.Join("; ", errors)));
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
