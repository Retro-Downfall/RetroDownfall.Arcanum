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

    /// <summary>
    /// The dedicated slot recording that this installation once enabled unsandboxed host-process
    /// tools.
    /// </summary>
    /// <remarks>
    /// Its own account precisely because it must outlive everything else. A database reset, a
    /// restore from an untainted archive, a master-key rotation, and an ordinary credential cleanup
    /// all leave this slot alone; only the attested full-installation reinitialize may compare-delete
    /// it. A marker folded into another secret would be erased by the first operation that rotated
    /// that secret, which is the evidence loss this second marker exists to prevent.
    /// </remarks>
    public const string HostProcessToolsTaintAccount = "host-process-tools-taint";

    /// <summary>OS credential account used by the native Perplexity web-research provider.</summary>
    public const string PerplexityApiKeyAccount = "provider-perplexity-api-key";

    /// <summary>
    /// Prefix owning the pre-database installation identity of one profile root's restore journal.
    /// </summary>
    /// <remarks>
    /// This trio is namespaced per profile root rather than fixed, because a restore's crash-recovery
    /// evidence is read before any database opens and one machine may hold several profile roots. A
    /// single shared account would let a journal, key, or anchor written under one root authenticate
    /// against another — and the thing that journal authorizes is renaming an installation.
    /// </remarks>
    public const string BackupRestoreJournalInstallationAccountPrefix =
        "backup-restore-journal-installation-";

    /// <summary>Prefix owning the AES-256-GCM key that authenticates one profile's restore journal.</summary>
    public const string BackupRestoreJournalKeyAccountPrefix = "backup-restore-journal-key-";

    /// <summary>Prefix owning one profile's anti-rollback restore-journal anchor.</summary>
    public const string BackupRestoreJournalAnchorAccountPrefix = "backup-restore-journal-anchor-";

    /// <summary>Prefix owning the AES-256-GCM key for one profile's installation-reset active record.</summary>
    public const string InstallationResetActiveKeyAccountPrefix =
        "installation-reset-active-key-";

    /// <summary>Prefix owning one profile's installation-reset active-record anti-rollback anchor.</summary>
    public const string InstallationResetActiveAnchorAccountPrefix =
        "installation-reset-active-anchor-";

    internal const string GrimoireTransitionJournalKeyAccountPrefix =
        "grimoire-transition-journal-key-";

    internal const string GrimoireTransitionJournalAnchorAccountPrefix =
        "grimoire-transition-journal-anchor-";

    /// <summary>
    /// The exact length of the lowercase-hex profile-namespace digest every restore-journal account is
    /// suffixed with.
    /// </summary>
    public const int ProfileNamespaceSuffixLength = 64;

    /// <summary>
    /// The installation-identity account for one profile namespace.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The suffix is not exactly <see cref="ProfileNamespaceSuffixLength"/> lowercase hex characters. A
    /// malformed suffix must never produce an account name, because the name that would come out of it
    /// is one this profile has no claim to.
    /// </exception>
    public static string BackupRestoreJournalInstallationAccount(string profileNamespaceDigestHex) =>
        BackupRestoreJournalInstallationAccountPrefix
        + RequireProfileNamespaceSuffix(profileNamespaceDigestHex);

    /// <summary>The journal-key account for one profile namespace.</summary>
    /// <exception cref="ArgumentException">The suffix is not a canonical profile-namespace digest.</exception>
    public static string BackupRestoreJournalKeyAccount(string profileNamespaceDigestHex) =>
        BackupRestoreJournalKeyAccountPrefix + RequireProfileNamespaceSuffix(profileNamespaceDigestHex);

    /// <summary>The anchor account for one profile namespace.</summary>
    /// <exception cref="ArgumentException">The suffix is not a canonical profile-namespace digest.</exception>
    public static string BackupRestoreJournalAnchorAccount(string profileNamespaceDigestHex) =>
        BackupRestoreJournalAnchorAccountPrefix + RequireProfileNamespaceSuffix(profileNamespaceDigestHex);

    /// <summary>The installation-reset active-record key account for one profile namespace.</summary>
    /// <exception cref="ArgumentException">The suffix is not a canonical profile-namespace digest.</exception>
    internal static string InstallationResetActiveKeyAccount(string profileSuffix) =>
        InstallationResetActiveKeyAccountPrefix + RequireProfileNamespaceSuffix(profileSuffix);

    /// <summary>The installation-reset active-record anchor account for one profile namespace.</summary>
    /// <exception cref="ArgumentException">The suffix is not a canonical profile-namespace digest.</exception>
    internal static string InstallationResetActiveAnchorAccount(string profileSuffix) =>
        InstallationResetActiveAnchorAccountPrefix + RequireProfileNamespaceSuffix(profileSuffix);

    internal static string GrimoireTransitionJournalKeyAccount(string profileSuffix) =>
        GrimoireTransitionJournalKeyAccountPrefix + RequireProfileNamespaceSuffix(profileSuffix);

    internal static string GrimoireTransitionJournalAnchorAccount(string profileSuffix) =>
        GrimoireTransitionJournalAnchorAccountPrefix + RequireProfileNamespaceSuffix(profileSuffix);

    /// <summary>
    /// True when <paramref name="account"/> is one of the two namespaced installation-reset active
    /// record accounts.
    /// </summary>
    internal static bool IsInstallationResetActiveAccount(string account)
    {

        if (account is null)
        {

            return false;

        }

        foreach (string prefix in (string[])
                 [
                     InstallationResetActiveKeyAccountPrefix,
                     InstallationResetActiveAnchorAccountPrefix,
                 ])
        {

            if (account.Length == prefix.Length + ProfileNamespaceSuffixLength
                && account.StartsWith(prefix, StringComparison.Ordinal)
                && IsCanonicalProfileNamespaceSuffix(account[prefix.Length..]))
            {

                return true;

            }

        }

        return false;

    }

    internal static bool IsGrimoireTransitionJournalAccount(string? account)
    {

        if (account is null)
        {

            return false;

        }

        foreach (string prefix in (string[])
                 [
                     GrimoireTransitionJournalKeyAccountPrefix,
                     GrimoireTransitionJournalAnchorAccountPrefix,
                 ])
        {

            if (account.Length == prefix.Length + ProfileNamespaceSuffixLength
                && account.StartsWith(prefix, StringComparison.Ordinal)
                && IsCanonicalProfileNamespaceSuffix(account[prefix.Length..]))
            {

                return true;

            }

        }

        return false;

    }

    /// <summary>
    /// True when <paramref name="account"/> is one of the three namespaced restore-journal accounts.
    /// </summary>
    /// <remarks>
    /// Requires the complete namespaced form. A bare prefix, an unnamespaced alias, or a suffix that is
    /// not canonical lowercase hex is not one of ours, and answering otherwise would let an inventory
    /// pass treat an arbitrary account as a restore credential.
    /// </remarks>
    public static bool IsBackupRestoreJournalAccount(string? account)
    {

        if (account is null)
        {

            return false;

        }

        foreach (string prefix in (string[])
                 [
                     BackupRestoreJournalInstallationAccountPrefix,
                     BackupRestoreJournalKeyAccountPrefix,
                     BackupRestoreJournalAnchorAccountPrefix,
                 ])
        {

            if (account.Length == prefix.Length + ProfileNamespaceSuffixLength
                && account.StartsWith(prefix, StringComparison.Ordinal)
                && IsCanonicalProfileNamespaceSuffix(account[prefix.Length..]))
            {

                return true;

            }

        }

        return false;

    }

    /// <summary>
    /// True when <paramref name="suffix"/> is exactly 64 lowercase hex characters.
    /// </summary>
    /// <remarks>
    /// Lowercase only, so one profile namespace has exactly one account name. Accepting both cases
    /// would give every profile two accounts, and a write to one would be invisible to a read of the
    /// other.
    /// </remarks>
    public static bool IsCanonicalProfileNamespaceSuffix(string? suffix)
    {

        if (suffix is null || suffix.Length != ProfileNamespaceSuffixLength)
        {

            return false;

        }

        foreach (char value in suffix)
        {

            if (value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {

                return false;

            }

        }

        return true;

    }

    private static string RequireProfileNamespaceSuffix(string profileNamespaceDigestHex) =>
        IsCanonicalProfileNamespaceSuffix(profileNamespaceDigestHex)
            ? profileNamespaceDigestHex
            : throw new ArgumentException(
                "A restore-journal credential account requires exactly "
                + ProfileNamespaceSuffixLength
                + " lowercase hex characters of profile-namespace digest.",
                nameof(profileNamespaceDigestHex));

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
