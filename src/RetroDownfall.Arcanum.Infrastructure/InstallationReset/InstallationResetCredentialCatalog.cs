using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed partial class InstallationResetCredentialCatalog(
    IOsCredentialStore credentialStore,
    ArcanumSettings? settings = null,
    string? secretStoreRoot = null) : IInstallationResetCredentialService
{
    private const string MirrorPrefix = "provider-";

    private const string MirrorSuffix = "-key.dat";

    public static string[] CollectAccounts(
        ArcanumSettings settings,
        string secretStoreRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The Campaign root-identity key and the memory-erasure fingerprint key are deleted here and
        // nowhere else. The documented lifetime of each ends only at a full installation reset, and
        // this catalog is the only delete path that exists: the credential store answers by account
        // name and cannot be enumerated, so an account this seed omits survives every reset the
        // operator can run. The restore-journal trio and the host-process-tools taint marker are
        // deliberately absent — their retention across a reset is the point of them (§10.19.6,
        // §11.2.1).
        HashSet<string> accounts = new(StringComparer.Ordinal)
        {
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            ArcanumCredentialIdentity.FileEncryptionKeyAccount,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount,
            ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount,
            ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount,
        };

        foreach (ProviderSettings provider in settings.Providers ?? [])
        {
            if (!FamiliarProviders.IsFamiliar(provider))
            {
                accounts.Add(
                    ArcanumCredentialIdentity.InferenceProviderApiKeyAccount(
                        provider.Name));
            }
        }

        try
        {
            if (Directory.Exists(secretStoreRoot))
            {
                foreach (string path in Directory.EnumerateFiles(
                             secretStoreRoot,
                             MirrorPrefix + "*" + MirrorSuffix,
                             SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(path);

                    Match match = ProviderMirrorName().Match(name);

                    if (match.Success)
                    {
                        accounts.Add(
                            ArcanumCredentialIdentity.InferenceProviderAccountPrefix
                            + match.Groups["provider"].Value
                            + ArcanumCredentialIdentity.InferenceProviderAccountSuffix);
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Configuration and fixed identities remain a bounded, valid inventory.
        }

        return CollectOrdinaryAccounts(accounts);
    }

    internal static string[] CollectOrdinaryAccounts(IEnumerable<string> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        return [.. accounts
            .Where(static account => !IsRetainedEvidenceAccount(account))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether an account holds evidence an ordinary installation reset must never remove.
    /// </summary>
    /// <remarks>
    /// The one predicate behind both the inventory and the delete. Applying it only when the inventory
    /// is built left the delete trusting whatever names it was handed, so anything that reached it
    /// without going through the inventory (an unsealed legacy record, a future caller) could remove
    /// the restore journal, the reset record's own key, or the host-tools taint marker.
    /// </remarks>
    internal static bool IsRetainedEvidenceAccount(string account) =>
        ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(account)
        || ArcanumCredentialIdentity.IsInstallationResetActiveAccount(account)
        || ArcanumCredentialIdentity.IsGrimoireTransitionJournalAccount(account)
        || string.Equals(
            account,
            ArcanumCredentialIdentity.HostProcessToolsTaintAccount,
            StringComparison.Ordinal);

    public InstallationResetCredentialSummary[] Probe(string[] accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        return [.. accounts
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(account =>
            {
                OsCredentialStoreResult result = credentialStore.TryGet(
                    ArcanumCredentialIdentity.Service,
                    account);

                return new InstallationResetCredentialSummary(
                    account,
                    MapProbeStatus(result.Status),
                    MapErrorCode(result.Status));
            })];
    }

    public InstallationResetCredentialSummary[] Probe() =>
        Probe(CollectAccounts(
            settings ?? new ArcanumSettings(),
            secretStoreRoot ?? ArcanumPaths.SecretStoreDirectory));

    public InstallationResetCredentialResult[] DeleteAndVerify(string[] accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        List<InstallationResetCredentialResult> results = [];

        foreach (string account in accounts
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            // Refused before the store is touched at all, per account, so one retained name never costs
            // the ordinary accounts beside it. Planning still reports these accounts when asked about
            // them; only the path that removes something refuses.
            if (IsRetainedEvidenceAccount(account))
            {
                results.Add(new InstallationResetCredentialResult(
                    account,
                    InstallationResetItemStatus.Failed,
                    Core.Primitives.ErrorCodes.Data.Blocked));

                continue;
            }

            OsCredentialStoreResult before = credentialStore.TryGet(
                ArcanumCredentialIdentity.Service,
                account);

            if (before.Status is OsCredentialStoreStatus.NotFound)
            {
                results.Add(new InstallationResetCredentialResult(
                    account,
                    InstallationResetItemStatus.Absent));

                continue;
            }

            if (before.Status is not OsCredentialStoreStatus.Ok)
            {
                results.Add(new InstallationResetCredentialResult(
                    account,
                    MapProbeStatus(before.Status),
                    MapErrorCode(before.Status)));

                continue;
            }

            OsCredentialStoreResult deleted = credentialStore.Delete(
                ArcanumCredentialIdentity.Service,
                account);

            if (deleted.Status is not OsCredentialStoreStatus.Ok
                and not OsCredentialStoreStatus.NotFound)
            {
                results.Add(new InstallationResetCredentialResult(
                    account,
                    MapDeleteFailure(deleted.Status),
                    MapErrorCode(deleted.Status)));

                continue;
            }

            OsCredentialStoreResult after = credentialStore.TryGet(
                ArcanumCredentialIdentity.Service,
                account);

            results.Add(new InstallationResetCredentialResult(
                account,
                after.Status is OsCredentialStoreStatus.NotFound
                    ? InstallationResetItemStatus.Deleted
                    : MapDeleteFailure(after.Status),
                after.Status is OsCredentialStoreStatus.NotFound
                    ? null
                    : MapErrorCode(after.Status)));
        }

        return [.. results];
    }

    public InstallationResetCredentialResult[] DeleteAndVerify() =>
        DeleteAndVerify(CollectAccounts(
            settings ?? new ArcanumSettings(),
            secretStoreRoot ?? ArcanumPaths.SecretStoreDirectory));

    private static InstallationResetItemStatus MapProbeStatus(
        OsCredentialStoreStatus status) =>
        status switch
        {
            OsCredentialStoreStatus.Ok => InstallationResetItemStatus.Pending,
            OsCredentialStoreStatus.NotFound => InstallationResetItemStatus.Absent,
            OsCredentialStoreStatus.Unavailable => InstallationResetItemStatus.Unavailable,
            _ => InstallationResetItemStatus.Failed,
        };

    private static InstallationResetItemStatus MapDeleteFailure(
        OsCredentialStoreStatus status) =>
        status is OsCredentialStoreStatus.Unavailable
            ? InstallationResetItemStatus.Unavailable
            : InstallationResetItemStatus.Failed;

    private static string? MapErrorCode(OsCredentialStoreStatus status) =>
        status switch
        {
            OsCredentialStoreStatus.Unavailable =>
                Core.Primitives.ErrorCodes.Data.CredentialInventoryUnavailable,
            OsCredentialStoreStatus.Failed =>
                Core.Primitives.ErrorCodes.Data.ReconciliationFailed,
            _ => null,
        };

    [GeneratedRegex(
        "^provider-(?<provider>[A-Z0-9]+(?:_[A-Z0-9]+)*)-key\\.dat$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProviderMirrorName();
}
