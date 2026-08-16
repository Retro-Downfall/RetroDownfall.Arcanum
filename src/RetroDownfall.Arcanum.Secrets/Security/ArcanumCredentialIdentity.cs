using System.Text;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// Fixed OS credential identity shared by Arcanum, The Forge, and any other local client that must
/// present the master <c>X-Arcanum-Key</c>. This is the shared "location" — not a file path.
/// </summary>
public static class ArcanumCredentialIdentity
{

    /// <summary>OS credential service / target name (Keychain service, Cred target namespace, libsecret schema).</summary>
    public const string Service = "arcanum";

    /// <summary>OS credential account / username distinguishing the master API key from future secrets.</summary>
    public const string MasterApiKeyAccount = "master-api-key";

    /// <summary>Dedicated master key for encrypted attachment/upload/batch blobs.</summary>
    public const string FileEncryptionKeyAccount = "file-encryption-master-key";

    /// <summary>
    /// Installation-private key that turns a physical directory into an opaque Campaign root identity.
    /// </summary>
    /// <remarks>
    /// Its own account rather than a derivation of the master API key, because its lifetime differs:
    /// API-key rotation and Covenant reset must both leave every registered Campaign root recognisable,
    /// and only a full installation reset regenerates it.
    /// </remarks>
    public const string CampaignRootIdentityKeyAccount = "campaign-root-identity-key";

    /// <summary>OS credential account used by the native Perplexity web-research provider.</summary>
    public const string PerplexityApiKeyAccount = "provider-perplexity-api-key";

    /// <summary>Prefix owning every per-inference-provider credential account.</summary>
    public const string InferenceProviderAccountPrefix = "inference-provider-";

    /// <summary>Suffix owning every per-inference-provider credential account.</summary>
    public const string InferenceProviderAccountSuffix = "-api-key";

    private const string UnnamedProvider = "UNNAMED";

    /// <summary>
    /// Deterministic OS credential account for one inference provider's API key. The provider name
    /// is normalized exactly like <c>ARCANUM_PROVIDER_{NAME}_API_KEY</c> so the environment
    /// reference and the secure-store identity always agree, and the dedicated
    /// <c>inference-provider-</c> prefix keeps a provider named "perplexity" from colliding with
    /// <see cref="PerplexityApiKeyAccount"/>.
    /// </summary>
    public static string InferenceProviderApiKeyAccount(string? providerName) =>
        InferenceProviderAccountPrefix
        + NormalizeProviderName(providerName)
        + InferenceProviderAccountSuffix;

    /// <summary>
    /// True when <paramref name="account"/> is an Arcanum-owned inference-provider credential
    /// account. Bulk credential operations use this instead of enumerating unrelated OS credentials.
    /// </summary>
    public static bool IsInferenceProviderApiKeyAccount(string? account) =>
        account is not null
        && account.StartsWith(InferenceProviderAccountPrefix, StringComparison.Ordinal)
        && account.EndsWith(InferenceProviderAccountSuffix, StringComparison.Ordinal)
        && account.Length
            > InferenceProviderAccountPrefix.Length + InferenceProviderAccountSuffix.Length;

    /// <summary>
    /// Converts a provider display name into a stable ASCII credential-account segment: letters are
    /// upper-cased, digits are retained, every run of other characters becomes one underscore, and
    /// an empty result becomes <c>UNNAMED</c>. Kept byte-for-byte identical to the environment
    /// variable normalization in <c>EnvironmentCredentialResolver.NormalizeProviderName</c>; Core
    /// cannot be referenced from Secrets, so the rule is asserted by
    /// <c>ProviderCredentialIdentityParityTests</c> instead.
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

}
