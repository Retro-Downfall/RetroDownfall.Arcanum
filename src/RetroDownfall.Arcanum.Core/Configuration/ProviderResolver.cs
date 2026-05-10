namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves which configured provider and model id to use for a request.
/// </summary>
public static class ProviderResolver
{

    /// <summary>
    /// Case-insensitive match between a configured model id and a requested id, including Ollama-style <c>:latest</c> tag stripping.
    /// </summary>
    public static bool ModelNameMatches(string configuredModel, string needle)
    {

        if (string.Equals(configuredModel, needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!needle.Contains(':'))
        {
            int colonIndex = configuredModel.IndexOf(':');

            if (colonIndex >= 0)
            {
                return configuredModel.AsSpan(0, colonIndex).Equals(needle, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;

    }

    /// <summary>
    /// Resolves provider and canonical model string. Fails if an explicit <paramref name="targetModel"/> or <see cref="ArcanumSettings.DefaultModel"/> is set but does not match any configured model.
    /// Internal background callers (for example Campaign Logger) pass <paramref name="targetModel"/> from <see cref="ArcanumSettings.FastModel"/> or <see cref="ArcanumSettings.DefaultModel"/>; those properties are not read here directly.
    /// </summary>
    public static bool TryResolveProviderForModel(
        ArcanumSettings settings,
        string? targetModel,
        out ProviderSettings? provider,
        out string resolvedModel)
    {

        provider = null;

        resolvedModel = string.Empty;

        ProviderSettings[] providers = settings.Providers ?? [];

        if (!string.IsNullOrWhiteSpace(targetModel))
        {
            string needle = targetModel.Trim();

            if (TryFindModelInProviders(providers, needle, out provider, out resolvedModel))
            {
                return true;
            }

            return false;

        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultModel))
        {
            string needle = settings.DefaultModel.Trim();

            if (TryFindModelInProviders(providers, needle, out provider, out resolvedModel))
            {
                return true;
            }

            return false;

        }

        if (providers.Length > 0)
        {
            string[] firstModels = providers[0].Models ?? [];

            if (firstModels.Length > 0)
            {
                provider = providers[0];

                resolvedModel = firstModels[0];

                return true;

            }

        }

        return false;

    }

    private static bool TryFindModelInProviders(
        ProviderSettings[] providers,
        string needle,
        out ProviderSettings? provider,
        out string resolvedModel)
    {

        provider = null;

        resolvedModel = string.Empty;

        for (int pi = 0; pi < providers.Length; pi++)
        {
            ProviderSettings p = providers[pi];

            string[] models = p.Models ?? [];

            for (int mi = 0; mi < models.Length; mi++)
            {
                string configured = models[mi];

                if (string.IsNullOrWhiteSpace(configured))
                {
                    continue;
                }

                if (ModelNameMatches(configured, needle))
                {
                    provider = p;

                    resolvedModel = configured;

                    return true;

                }

            }

        }

        return false;

    }

}
