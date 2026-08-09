namespace RetroDownfall.Arcanum.Core.Configuration;

/// <param name="Name">The provider row's operator-chosen name.</param>
/// <param name="Type">The <see cref="AiProviderKind"/> name.</param>
/// <param name="Endpoint">Redacted; empty for a Familiar, which has no endpoint.</param>
/// <param name="CredentialEnvironmentVariable">
/// The variable Arcanum would read for this provider's key. Empty for a Familiar: its CLI signs in
/// against the operator's own subscription and Arcanum never reads that credential store.
/// </param>
/// <param name="Models">Models this provider offers, with the hide list already applied.</param>
/// <param name="ContextWindowLimit">Configured context ceiling.</param>
/// <param name="HiddenModels">
/// The operator's hide list, reported beside the offered models so a decluttered listing is still
/// inspectable. Hidden is not blocked — an explicitly named model still resolves.
/// </param>
public sealed record ProviderInfoDto(
    string Name,
    string Type,
    string Endpoint,
    string CredentialEnvironmentVariable,
    string[] Models,
    int ContextWindowLimit,
    string[]? HiddenModels = null);
