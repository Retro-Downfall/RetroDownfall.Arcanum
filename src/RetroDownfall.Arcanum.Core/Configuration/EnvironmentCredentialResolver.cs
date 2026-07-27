using System.Text;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Resolves provider, HTTPS, and CommLink secrets from environment variables without copying
/// secret material into the bindable configuration graph.
/// </summary>
public static class EnvironmentCredentialResolver
{
    public const string DefaultHttpsCertificatePasswordEnvironmentVariable =
        "ARCANUM_HTTPS_CERTIFICATE_PASSWORD";

    public const string DefaultCommLinkWebhookUrlEnvironmentVariable =
        "ARCANUM_COMMLINK_WEBHOOK_URL";

    private const string ProviderPrefix = "ARCANUM_PROVIDER_";
    private const string ProviderSuffix = "_API_KEY";
    private const string UnnamedProvider = "UNNAMED";

    /// <summary>
    /// Returns the exact configured reference, or the deterministic provider-name-derived default.
    /// An explicit reference replaces the default rather than falling through to it.
    /// </summary>
    public static string GetProviderApiKeyEnvironmentVariableName(ProviderSettings provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return !string.IsNullOrWhiteSpace(provider.CredentialEnvironmentVariable)
            ? provider.CredentialEnvironmentVariable.Trim()
            : ProviderPrefix + NormalizeProviderName(provider.Name) + ProviderSuffix;
    }

    public static string? ResolveProviderApiKey(ProviderSettings provider) =>
        ReadEnvironmentVariable(GetProviderApiKeyEnvironmentVariableName(provider));

    /// <summary>
    /// Returns the exact configured certificate-password reference, or the deterministic HTTPS
    /// default. An explicit reference replaces the default rather than falling through to it.
    /// </summary>
    public static string GetHttpsCertificatePasswordEnvironmentVariableName(HttpsSettings https)
    {
        ArgumentNullException.ThrowIfNull(https);

        return !string.IsNullOrWhiteSpace(https.CertificatePasswordEnvironmentVariable)
            ? https.CertificatePasswordEnvironmentVariable.Trim()
            : DefaultHttpsCertificatePasswordEnvironmentVariable;
    }

    public static string? ResolveHttpsCertificatePassword(HttpsSettings https) =>
        ReadEnvironmentVariable(
            GetHttpsCertificatePasswordEnvironmentVariableName(https));

    /// <summary>
    /// Returns the exact configured CommLink webhook reference, or its deterministic default.
    /// The referenced URL is secret-bearing and is read only by the dispatcher.
    /// </summary>
    public static string GetCommLinkWebhookUrlEnvironmentVariableName(
        CommLinkSettings commLink)
    {
        ArgumentNullException.ThrowIfNull(commLink);

        return !string.IsNullOrWhiteSpace(commLink.WebhookUrlEnvironmentVariable)
            ? commLink.WebhookUrlEnvironmentVariable.Trim()
            : DefaultCommLinkWebhookUrlEnvironmentVariable;
    }

    public static string? ResolveCommLinkWebhookUrl(CommLinkSettings commLink) =>
        ReadEnvironmentVariable(
            GetCommLinkWebhookUrlEnvironmentVariableName(commLink));

    /// <summary>
    /// Converts a provider display name into a stable ASCII environment-variable segment:
    /// letters are upper-cased, digits are retained, every run of other characters becomes one
    /// underscore, and an empty result becomes <c>UNNAMED</c>.
    /// </summary>
    public static string NormalizeProviderName(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return UnnamedProvider;
        }

        StringBuilder normalized = new(providerName.Length);
        bool pendingSeparator = false;

        foreach (char value in providerName)
        {
            bool asciiLetter =
                value is >= 'A' and <= 'Z'
                || value is >= 'a' and <= 'z';
            bool asciiDigit = value is >= '0' and <= '9';

            if (asciiLetter || asciiDigit)
            {
                if (pendingSeparator && normalized.Length > 0)
                {
                    _ = normalized.Append('_');
                }

                _ = normalized.Append(
                    asciiLetter
                        ? char.ToUpperInvariant(value)
                        : value);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = normalized.Length > 0;
            }
        }

        return normalized.Length == 0
            ? UnnamedProvider
            : normalized.ToString();
    }

    public static bool IsValidEnvironmentVariableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !IsAsciiLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        for (int index = 1; index < name.Length; index++)
        {
            char value = name[index];
            bool asciiDigit = value is >= '0' and <= '9';

            if (!IsAsciiLetter(value)
                && !asciiDigit
                && value != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadEnvironmentVariable(string name)
    {
        if (!IsValidEnvironmentVariableName(name))
        {
            return null;
        }

        string? value = System.Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z'
        || value is >= 'a' and <= 'z';
}
