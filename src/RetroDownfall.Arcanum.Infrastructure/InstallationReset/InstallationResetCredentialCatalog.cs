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

        // The Campaign root-identity key is named here and nowhere else. Its documented lifetime is
        // "regenerated only by a full installation reset", and this catalog is the only delete path
        // that exists: the credential store answers by account name and cannot be enumerated, so an
        // account this seed omits survives every reset the operator can run. The restore-journal trio
        // and the host-process-tools taint marker are deliberately absent — their retention across a
        // reset is the point of them (§10.19.6, §11.2.1).
        HashSet<string> accounts = new(StringComparer.Ordinal)
        {
            ArcanumCredentialIdentity.MasterApiKeyAccount,
            ArcanumCredentialIdentity.FileEncryptionKeyAccount,
            ArcanumCredentialIdentity.PerplexityApiKeyAccount,
            ArcanumCredentialIdentity.CampaignRootIdentityKeyAccount,
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

        return [.. accounts
            .Where(static account =>
                !ArcanumCredentialIdentity.IsBackupRestoreJournalAccount(account)
                && !ArcanumCredentialIdentity.IsInstallationResetActiveAccount(account)
                && !string.Equals(
                    account,
                    ArcanumCredentialIdentity.HostProcessToolsTaintAccount,
                    StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    }

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
