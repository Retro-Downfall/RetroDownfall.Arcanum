using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Familiars;

/// <summary>
/// Every environment variable Arcanum has been told holds one of <em>its</em> secrets, named so a
/// Familiar's child process never inherits it.
/// </summary>
/// <remarks>
/// <para>
/// Most of these default to an <c>ARCANUM_</c>-prefixed name that the standard scrub already
/// removes. The operator can point any of them somewhere else, though, and a variable called
/// <c>MY_OPENAI_KEY</c> is no less Arcanum's secret for being named that — so the list is built from
/// configuration rather than assumed from a prefix.
/// </para>
/// <para>
/// The Familiar's <em>own</em> vendor credentials are deliberately absent. Arcanum invokes a
/// Familiar; it does not decide how that Familiar authenticates, and stripping the CLI's own auth
/// variables would be managing it.
/// </para>
/// </remarks>
public static class FamiliarSecretEnvironmentNames
{

    public static IReadOnlyList<string> Collect(ArcanumSettings settings)
    {

        ArgumentNullException.ThrowIfNull(settings);

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProviderSettings provider in settings.Providers ?? [])
        {

            if (FamiliarProviders.IsFamiliar(provider))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(provider.CredentialEnvironmentVariable))
            {
                _ = names.Add(provider.CredentialEnvironmentVariable.Trim());
            }

            // Both spellings: a derived variable can still be set in the operator's shell after they
            // switched that provider to an explicit reference.
            _ = names.Add(EnvironmentCredentialResolver.GetProviderApiKeyEnvironmentVariableName(provider));

            _ = names.Add(EnvironmentCredentialResolver.GetDerivedProviderApiKeyEnvironmentVariableName(provider));

        }

        _ = names.Add(EnvironmentCredentialResolver.GetHttpsCertificatePasswordEnvironmentVariableName(
            settings.Host?.Https ?? new HttpsSettings()));

        _ = names.Add(EnvironmentCredentialResolver.GetCommLinkWebhookUrlEnvironmentVariableName(
            settings.ResolveCommLink()));

        _ = names.Add(EnvironmentCredentialResolver.GetWebResearchApiKeyEnvironmentVariableName(
            settings.ResolveWebBrowsing()));

        if (settings.Integrations?.A2A?.OutboundCredentialEnvironmentVariable is { } outbound
            && !string.IsNullOrWhiteSpace(outbound))
        {
            _ = names.Add(outbound.Trim());
        }

        return [.. names];

    }

}
