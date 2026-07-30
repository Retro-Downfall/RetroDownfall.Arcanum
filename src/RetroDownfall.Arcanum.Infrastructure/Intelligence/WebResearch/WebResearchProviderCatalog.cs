using System.Diagnostics.CodeAnalysis;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

/// <summary>
/// Immutable, case-insensitive catalog of registered native web-research providers.
/// </summary>
public sealed class WebResearchProviderCatalog : IWebResearchProviderCatalog
{
    private readonly IReadOnlyDictionary<string, IWebResearchProvider> _providers;

    public WebResearchProviderCatalog(IEnumerable<IWebResearchProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        Dictionary<string, IWebResearchProvider> catalog =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (IWebResearchProvider provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);

            if (string.IsNullOrWhiteSpace(provider.ProviderName))
            {
                throw new InvalidOperationException(
                    "A web-research provider cannot have an empty name.");
            }

            string name = provider.ProviderName.Trim();

            if (!catalog.TryAdd(name, provider))
            {
                throw new InvalidOperationException(
                    $"More than one web-research provider is registered as '{name}'.");
            }
        }

        _providers = catalog;
    }

    public bool TryGetProvider(
        string providerName,
        [NotNullWhen(true)] out IWebResearchProvider? provider)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            provider = null;
            return false;
        }

        return _providers.TryGetValue(providerName.Trim(), out provider);
    }
}
