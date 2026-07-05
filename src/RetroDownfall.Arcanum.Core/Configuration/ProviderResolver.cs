using RetroDownfall.Arcanum.Core.Resilience;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves which configured provider and model id to use for a request.
/// </summary>
public static class ProviderResolver
{

    /// <summary>
    /// Case-insensitive exact match between a configured model id and a requested id. The requested
    /// model name must exactly match (case-insensitive) a configured model id — there is no bare-name
    /// or tag-stripping fallback.
    /// </summary>
    public static bool ModelNameMatches(string configuredModel, string needle) =>
        string.Equals(configuredModel, needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the union of <see cref="ProviderSettings.Models"/> and, for <see cref="AiProviderKind.LlamaCppServer"/> providers, <c>LlamaCpp.ModelMap</c> keys.
    /// </summary>
    public static IEnumerable<string> EnumerateAdvertisedModels(ProviderSettings provider)
    {

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ModelEntry> models = provider.Models ?? [];

        for (int i = 0; i < models.Count; i++)
        {
            string model = models[i].Name;

            if (!string.IsNullOrWhiteSpace(model) && seen.Add(model))
            {
                yield return model;
            }
        }

        if (provider.Type != AiProviderKind.LlamaCppServer)
        {
            yield break;
        }

        Dictionary<string, string>? map = provider.LlamaCpp?.ModelMap;

        if (map is null)
        {
            yield break;
        }

        foreach (string key in map.Keys)
        {
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
            {
                yield return key;
            }
        }

    }

    /// <summary>
    /// Resolves whether <paramref name="modelName"/> declares Scrying (vision) support on any
    /// configured provider's <see cref="ModelEntry.SupportsVision"/>. Models advertised only via
    /// <c>LlamaCpp.ModelMap</c> (no matching <see cref="ProviderSettings.Models"/> entry) are
    /// never vision-capable — capability is declared exclusively through <see cref="ModelEntry"/>.
    /// </summary>
    public static bool SupportsVision(ArcanumSettings settings, string? modelName)
    {

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        ProviderSettings[] providers = settings.Providers ?? [];

        for (int pi = 0; pi < providers.Length; pi++)
        {
            if (TryFindModelEntry(providers[pi], modelName, out ModelEntry? entry) && entry!.SupportsVision)
            {
                return true;
            }
        }

        return false;

    }

    /// <summary>
    /// Resolves whether <paramref name="modelName"/> declares Scrying (vision) support on the
    /// specific <paramref name="provider"/> (post-resolution overload — avoids re-scanning every
    /// configured provider when the caller already knows which one was selected).
    /// </summary>
    public static bool SupportsVision(ProviderSettings provider, string? modelName)
    {

        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        return TryFindModelEntry(provider, modelName, out ModelEntry? entry) && entry!.SupportsVision;

    }

    private static bool TryFindModelEntry(ProviderSettings provider, string modelName, out ModelEntry? entry)
    {

        IReadOnlyList<ModelEntry> models = provider.Models ?? [];

        for (int i = 0; i < models.Count; i++)
        {
            if (ModelNameMatches(models[i].Name, modelName))
            {
                entry = models[i];

                return true;
            }
        }

        entry = null;

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
            foreach (string model in EnumerateAdvertisedModels(providers[0]))
            {
                provider = providers[0];

                resolvedModel = model;

                return true;
            }

        }

        return false;

    }

    /// <summary>
    /// Resolves the model and returns every configured provider that advertises it, in configured
    /// (array-index) order. Backward compatible: when <paramref name="health"/> is <c>null</c>, returns
    /// at most one candidate — the same provider <see cref="TryResolveProviderForModel"/> would pick.
    /// When <paramref name="health"/> is supplied, providers reported unhealthy are excluded from the
    /// result; if that would leave zero candidates, every match is returned anyway (not just the
    /// first) so the caller's runtime connectivity fallback can still rotate through all configured
    /// candidates — health tracking can be stale (e.g. all matches transiently marked unhealthy by a
    /// slow probe interval) and returning only one candidate would prevent
    /// <c>WizardIntelligenceProvider</c>'s fallback loop from ever trying the others, collapsing
    /// <c>Resilience.MaxFallbackAttempts</c> down to a single attempt regardless of configuration.
    /// </summary>
    public static IReadOnlyList<(ProviderSettings Provider, string CanonicalModelId)> ResolveCandidates(
        ArcanumSettings settings,
        string? model,
        IProviderHealthTracker? health = null)
    {

        ProviderSettings[] providers = settings.Providers ?? [];

        string? needle = !string.IsNullOrWhiteSpace(model)
            ? model.Trim()
            : !string.IsNullOrWhiteSpace(settings.DefaultModel)
                ? settings.DefaultModel.Trim()
                : null;

        List<(ProviderSettings Provider, string CanonicalModelId)> matches = needle is not null
            ? CollectAllMatchingProviders(providers, needle)
            : CollectFirstProviderDefaultModel(providers);

        if (matches.Count == 0)
        {
            return [];
        }

        if (health is null)
        {
            return [matches[0]];
        }

        List<(ProviderSettings Provider, string CanonicalModelId)> healthyMatches = [];

        foreach ((ProviderSettings Provider, string CanonicalModelId) candidate in matches)
        {
            if (health.IsHealthy(candidate.Provider.Name))
            {
                healthyMatches.Add(candidate);
            }

        }

        return healthyMatches.Count > 0 ? healthyMatches : matches;

    }

    private static List<(ProviderSettings Provider, string CanonicalModelId)> CollectAllMatchingProviders(
        ProviderSettings[] providers,
        string needle)
    {

        List<(ProviderSettings Provider, string CanonicalModelId)> matches = [];

        for (int pi = 0; pi < providers.Length; pi++)
        {
            ProviderSettings p = providers[pi];

            foreach (string configured in EnumerateAdvertisedModels(p))
            {
                if (ModelNameMatches(configured, needle))
                {
                    matches.Add((p, configured));

                    break;

                }

            }

        }

        return matches;

    }

    private static List<(ProviderSettings Provider, string CanonicalModelId)> CollectFirstProviderDefaultModel(
        ProviderSettings[] providers)
    {

        List<(ProviderSettings Provider, string CanonicalModelId)> matches = [];

        if (providers.Length == 0)
        {
            return matches;
        }

        foreach (string model in EnumerateAdvertisedModels(providers[0]))
        {
            matches.Add((providers[0], model));

            break;

        }

        return matches;

    }

    /// <summary>
    /// Resolves a configured provider by its exact (case-insensitive) <see cref="ProviderSettings.Name"/>.
    /// Used by the Weave embedding generator factory and by <see cref="ConfigurationValidator"/> to
    /// validate <c>Arcanum:Embeddings:Provider</c> — unlike <see cref="TryResolveProviderForModel"/>,
    /// this looks up by provider name, not by advertised model id.
    /// </summary>
    public static bool TryResolveProviderByName(
        ArcanumSettings settings,
        string providerName,
        out ProviderSettings? provider)
    {

        provider = null;

        ProviderSettings[] providers = settings.Providers ?? [];

        for (int i = 0; i < providers.Length; i++)
        {
            if (string.Equals(providers[i].Name, providerName, StringComparison.OrdinalIgnoreCase))
            {
                provider = providers[i];

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

            foreach (string configured in EnumerateAdvertisedModels(p))
            {
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
