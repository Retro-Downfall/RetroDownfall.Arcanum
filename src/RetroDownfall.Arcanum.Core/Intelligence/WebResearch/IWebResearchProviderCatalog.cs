using System.Diagnostics.CodeAnalysis;

namespace RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

/// <summary>
/// Case-insensitive catalog of registered web-research providers. Implementations must reject
/// duplicate provider names rather than selecting one by registration order.
/// </summary>
public interface IWebResearchProviderCatalog
{
    bool TryGetProvider(
        string providerName,
        [NotNullWhen(true)] out IWebResearchProvider? provider);
}
